using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkTrafficController.Core.Mapping;
using NetworkTrafficController.Core.Monitoring;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Monitoring;

public class MonitorEngineTests
{
    private static readonly NetAddress Local = NetAddress.FromIPv4Bytes([10, 0, 0, 1]);
    private static readonly NetAddress Remote = NetAddress.FromIPv4Bytes([10, 0, 0, 2]);

    private sealed class FakeFlowSource : IFlowEventSource
    {
        private readonly BlockingCollection<FlowEvent> _queue = new();
        public bool ThrowOnShutdown { get; init; }
        public int ShutdownCalls;
        public int DisposeCalls;

        public void Enqueue(FlowEvent ev) => _queue.Add(ev);

        public bool TryReceive(out FlowEvent flowEvent)
        {
            try
            {
                flowEvent = _queue.Take();
                return true;
            }
            catch (InvalidOperationException)
            {
                flowEvent = default;
                return false;
            }
        }

        public void Shutdown()
        {
            Interlocked.Increment(ref ShutdownCalls);
            _queue.CompleteAdding();
            if (ThrowOnShutdown)
            {
                throw new InvalidOperationException("boom");
            }
        }

        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
    }

    private sealed class FakePacketSource : IPacketSource
    {
        private readonly BlockingCollection<(byte[] Data, PacketMeta[] Metas)> _queue = new();
        public int DisposeCalls;

        public void Enqueue(byte[] data, params PacketMeta[] metas) => _queue.Add((data, metas));

        public bool TryReceiveBatch(Span<byte> buffer, Span<PacketMeta> metas, out int bytesReceived, out int packetCount)
        {
            bytesReceived = 0;
            packetCount = 0;
            try
            {
                var (data, m) = _queue.Take();
                data.CopyTo(buffer);
                m.CopyTo(metas);
                bytesReceived = data.Length;
                packetCount = m.Length;
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public void Shutdown() => _queue.CompleteAdding();

        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
    }

    private sealed class FakeSnapshots : IConnectionSnapshotProvider
    {
        public List<TcpConnectionRow> Tcp { get; } = new();
        public int Calls;
        public IReadOnlyList<TcpConnectionRow> GetTcpConnections() { Interlocked.Increment(ref Calls); return Tcp; }
        public IReadOnlyList<UdpEndpointRow> GetUdpEndpoints() => [];
    }

    private static (MonitorEngine Engine, FakeFlowSource Flow, FakePacketSource Packets, FakeSnapshots Snap) Create(bool throwOnShutdown = false)
    {
        var time = new FakeTimeProvider();
        var flow = new FakeFlowSource { ThrowOnShutdown = throwOnShutdown };
        var packets = new FakePacketSource();
        var snap = new FakeSnapshots();
        var engine = new MonitorEngine(
            flow, packets, snap,
            new FlowTable(new FlowTableOptions(), time, NullLogger.Instance),
            new MonitorAggregator(new MonitorAggregatorOptions(), time),
            new MonitorEngineOptions { SnapshotInterval = TimeSpan.FromHours(1) },
            time,
            NullLogger.Instance);
        return (engine, flow, packets, snap);
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    [Fact]
    public void Start_AppliesInitialSnapshot_BeforeReceiving()
    {
        var (engine, _, _, snap) = Create();
        var key = new FlowKey(IpProtocol.Tcp, Local, 1000, Remote, 80);
        snap.Tcp.Add(new TcpConnectionRow(key, 42, TcpState.Established));

        using (engine)
        {
            engine.Start();
            Assert.Equal(1, snap.Calls);
            Assert.True(engine.FlowTable.TryResolve(key, out uint pid, out _));
            Assert.Equal(42u, pid);
        }
    }

    [Fact]
    public void FlowEvents_UpdateTable_AndPacketsAreAttributed()
    {
        var (engine, flow, packets, _) = Create();
        var key = new FlowKey(IpProtocol.Tcp, Local, 50000, Remote, 443);
        using (engine)
        {
            engine.Start();
            flow.Enqueue(new FlowEvent(true, key, 7, 1, true, false, default, 0));
            Assert.True(WaitUntil(() => engine.FlowEventCount == 1));

            var a = PacketParserTests.IPv4(IpProtocol.Tcp, [10, 0, 0, 1], [10, 0, 0, 2], 50000, 443, 100);
            var b = PacketParserTests.IPv4(IpProtocol.Tcp, [10, 0, 0, 2], [10, 0, 0, 1], 443, 50000, 10);
            packets.Enqueue(a.Concat(b).ToArray(), new PacketMeta(0, true, false, false), new PacketMeta(0, false, false, false));
            Assert.True(WaitUntil(() => engine.PacketCount == 2));
            Assert.True(WaitUntil(() => engine.Aggregator.Snapshot().Processes.Count == 1));

            var p = Assert.Single(engine.Aggregator.Snapshot().Processes);
            Assert.Equal(7u, p.Pid);
            Assert.Equal(a.Length, p.Counters.OutBytes);
            Assert.Equal(b.Length, p.Counters.InBytes);
        }
    }

    [Fact]
    public void ProcessBatch_StopsAtCorruptBoundary_AndCountsParseFailure()
    {
        var (engine, _, _, _) = Create();
        var good = PacketParserTests.IPv4(IpProtocol.Udp, [10, 0, 0, 1], [10, 0, 0, 2], 1, 2, 5);
        var data = good.Concat(new byte[] { 0xFF, 0x00 }).ToArray();
        var metas = new[] { new PacketMeta(0, true, false, false), new PacketMeta(0, true, false, false), new PacketMeta(0, true, false, false) };

        engine.ProcessBatch(data, metas);

        var s = engine.Aggregator.Snapshot();
        Assert.Equal(1, s.Total.OutPackets);
        Assert.Equal(1, s.ParseFailures);
        engine.Dispose();
    }

    [Fact]
    public void ProcessBatch_WithMoreMetasThanData_DoesNotThrow()
    {
        var (engine, _, _, _) = Create();
        var metas = new[] { new PacketMeta(0, true, false, false), new PacketMeta(0, true, false, false) };
        engine.ProcessBatch(ReadOnlySpan<byte>.Empty, metas);
        Assert.Equal(1, engine.Aggregator.Snapshot().ParseFailures);
        engine.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent_AndShutsDownSources()
    {
        var (engine, flow, packets, _) = Create();
        engine.Start();
        engine.Dispose();
        engine.Dispose();

        Assert.Equal(1, flow.ShutdownCalls);
        Assert.Equal(1, flow.DisposeCalls);
        Assert.Equal(1, packets.DisposeCalls);
    }

    [Fact]
    public void Dispose_ContinuesWhenShutdownThrows()
    {
        var (engine, flow, packets, _) = Create(throwOnShutdown: true);
        engine.Start();
        engine.Dispose();

        Assert.Equal(1, flow.DisposeCalls);
        Assert.Equal(1, packets.DisposeCalls);
    }
}
