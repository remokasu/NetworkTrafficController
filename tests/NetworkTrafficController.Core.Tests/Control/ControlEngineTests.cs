using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkTrafficController.Core.Control;
using NetworkTrafficController.Core.Mapping;
using NetworkTrafficController.Core.Monitoring;
using NetworkTrafficController.Core.Tests.Monitoring;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Control;

public class ControlEngineTests
{
    private static readonly NetAddress Local = NetAddress.FromIPv4Bytes([192, 168, 1, 10]);
    private static readonly NetAddress Remote = NetAddress.FromIPv4Bytes([1, 1, 1, 1]);
    private static readonly NetAddress Lo = NetAddress.FromIPv4Bytes([127, 0, 0, 1]);

    private sealed class FakeToken : ControlPacketToken
    {
        public FakeToken(byte[] data, PacketMeta meta)
        {
            Bytes = data;
            MetaValue = meta;
        }

        public byte[] Bytes { get; }
        public PacketMeta MetaValue { get; }
        public override ReadOnlyMemory<byte> Data => Bytes;
        public override PacketMeta Meta => MetaValue;
    }

    private sealed class FakeChannel : IControlChannel
    {
        private readonly BlockingCollection<ControlPacketToken> _incoming = new();
        public ConcurrentQueue<ControlPacketToken> Sent { get; } = new();
        public ConcurrentQueue<ControlPacketToken> Released { get; } = new();
        public ConcurrentQueue<byte[]> Injected { get; } = new();
        public int ShutdownCalls;
        public int DisposeCalls;

        public void Feed(ControlPacketToken token) => _incoming.Add(token);

        public bool TryReceive(out ControlPacketToken token)
        {
            try
            {
                token = _incoming.Take();
                return true;
            }
            catch (InvalidOperationException)
            {
                token = null!;
                return false;
            }
        }

        public void Send(ControlPacketToken token) => Sent.Enqueue(token);

        public void Release(ControlPacketToken token) => Released.Enqueue(token);

        public bool InjectReverse(ControlPacketToken original, ReadOnlySpan<byte> packet)
        {
            Injected.Enqueue(packet.ToArray());
            return true;
        }

        public void Shutdown()
        {
            Interlocked.Increment(ref ShutdownCalls);
            _incoming.CompleteAdding();
        }

        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
    }

    private sealed record Fixture(ControlEngine Engine, FakeChannel Channel, FlowTable Table, ControlPolicyStore Store, FakeTimeProvider Time);

    private static Fixture Create()
    {
        var time = new FakeTimeProvider();
        var table = new FlowTable(new FlowTableOptions(), time, NullLogger.Instance);
        var store = new ControlPolicyStore(new ProcessNameResolver(NullLogger.Instance, time), time);
        var channel = new FakeChannel();
        var engine = new ControlEngine(channel, table, store, new ControlStatistics(), time, NullLogger.Instance);
        return new Fixture(engine, channel, table, store, time);
    }

    private static FakeToken Outbound(ushort srcPort, ushort dstPort, int payload = 100) =>
        new(PacketParserTests.IPv4(IpProtocol.Tcp, [192, 168, 1, 10], [1, 1, 1, 1], srcPort, dstPort, payload), new PacketMeta(0, Outbound: true, Loopback: false, IsIPv6: false));

    private static FakeToken Inbound(ushort srcPort, ushort dstPort, int payload = 100) =>
        new(PacketParserTests.IPv4(IpProtocol.Tcp, [1, 1, 1, 1], [192, 168, 1, 10], srcPort, dstPort, payload), new PacketMeta(0, Outbound: false, Loopback: false, IsIPv6: false));

    [Fact]
    public void NonTargetPacket_IsReinjectedUnchanged()
    {
        var f = Create();
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Local, 50000, Remote, 443), 100, 0);
        var token = Outbound(50000, 443);

        f.Engine.Process(token);

        Assert.Single(f.Channel.Sent);
        Assert.Same(token, f.Channel.Sent.Single());
        Assert.Empty(f.Channel.Released);
        f.Engine.Dispose();
    }

    [Fact]
    public void UnresolvedPacket_IsReinjected_FailOpen()
    {
        var f = Create();
        f.Store.SetPidPolicy(100, new ControlPolicy { Block = true });

        f.Engine.Process(Outbound(1, 2)); // 照合表に無い 5-tuple

        Assert.Single(f.Channel.Sent);
        Assert.Empty(f.Channel.Released);
        f.Engine.Dispose();
    }

    [Fact]
    public void BlockedTarget_IsDropped_OtherProcessPasses()
    {
        var f = Create();
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Local, 50000, Remote, 443), 100, 0); // target
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Local, 50001, Remote, 443), 200, 0); // other
        f.Store.SetPidPolicy(100, new ControlPolicy { Block = true });

        var targetOut = Outbound(50000, 443);
        var targetIn = Inbound(443, 50000);
        var other = Outbound(50001, 443);
        f.Engine.Process(targetOut);
        f.Engine.Process(targetIn);
        f.Engine.Process(other);

        Assert.Equal(2, f.Channel.Released.Count);
        Assert.Contains(targetOut, f.Channel.Released);
        Assert.Contains(targetIn, f.Channel.Released);
        Assert.Single(f.Channel.Sent);
        Assert.Same(other, f.Channel.Sent.Single());
        Assert.Equal(2, f.Engine.Statistics.DroppedBlock);
        f.Engine.Dispose();
    }

    [Fact]
    public void UnparseablePacket_IsReinjected()
    {
        var f = Create();
        var garbage = new FakeToken([0xFF, 0x00, 0x01], new PacketMeta(0, true, false, false));

        f.Engine.Process(garbage);

        Assert.Same(garbage, f.Channel.Sent.Single());
        Assert.Equal(1, f.Engine.UnparsedPassed);
        f.Engine.Dispose();
    }

    [Fact]
    public void DelayedPacket_IsQueued_ThenFlushedOnDispose()
    {
        var f = Create();
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Local, 50000, Remote, 443), 100, 0);
        f.Store.SetPidPolicy(100, new ControlPolicy { Upload = BandwidthLimit.FromBytesPerSecond(1_000) });

        // 1 個目（65,040 B）で容量 65,575 のほぼ全部を使い、2 個目（1,040 B）でトークンが負になって遅延
        f.Engine.Process(Outbound(50000, 443, payload: 65_000));
        var delayed = Outbound(50000, 443, payload: 1_000);
        f.Engine.Process(delayed);

        Assert.Single(f.Channel.Sent);
        Assert.Equal(1, f.Engine.QueuedPackets);
        Assert.Equal(1, f.Engine.Statistics.DelayedPackets);

        f.Engine.Dispose(); // 期限前でも再注入して通信を落とさない

        Assert.Equal(2, f.Channel.Sent.Count);
        Assert.Contains(delayed, f.Channel.Sent);
        Assert.Empty(f.Channel.Released);
    }

    [Fact]
    public void Loopback_BothSidesEvaluated_ReceiverBlockDrops()
    {
        var f = Create();
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Lo, 40000, Lo, 8080), 11, 0); // client
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Lo, 8080, Lo, 40000), 22, 0); // server
        f.Store.SetPidPolicy(22, new ControlPolicy { Block = true, BlockDirection = ControlDirection.Inbound });

        var token = new FakeToken(
            PacketParserTests.IPv4(IpProtocol.Tcp, [127, 0, 0, 1], [127, 0, 0, 1], 40000, 8080, 10),
            new PacketMeta(0, Outbound: true, Loopback: true, IsIPv6: false));
        f.Engine.Process(token);

        Assert.Same(token, f.Channel.Released.Single()); // サーバーの受信 Block が効く
        Assert.Empty(f.Channel.Sent);
        f.Engine.Dispose();
    }

    [Fact]
    public void Loopback_DelayOnOneSide_DropOnOther_UndoesReservation()
    {
        var f = Create();
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Lo, 40000, Lo, 8080), 11, 0);
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Lo, 8080, Lo, 40000), 22, 0);
        f.Store.SetPidPolicy(11, new ControlPolicy { Upload = BandwidthLimit.FromBytesPerSecond(1_000) });
        f.Store.SetPidPolicy(22, new ControlPolicy { Block = true });

        // クライアントの上りバーストを使い切ってから（65,040 B）、遅延になるパケット（1,040 B）を送る。
        // 2 個目はクライアント側で遅延予約されるが、サーバー側 Block で全体が破棄になり予約が戻される
        var first = new FakeToken(PacketParserTests.IPv4(IpProtocol.Tcp, [127, 0, 0, 1], [127, 0, 0, 1], 40000, 8080, 65_000), new PacketMeta(0, true, true, false));
        f.Engine.Process(first);
        var second = new FakeToken(PacketParserTests.IPv4(IpProtocol.Tcp, [127, 0, 0, 1], [127, 0, 0, 1], 40000, 8080, 1_000), new PacketMeta(0, true, true, false));
        f.Engine.Process(second);

        Assert.Equal(2, f.Channel.Released.Count); // サーバー側 Block で両方破棄
        Assert.Equal(0, f.Engine.QueuedPackets);

        // 予約が戻っていれば、3 個目（65,040 B）はクライアント側でキュー上限（65,536）に収まり遅延判定になる。
        // 戻っていなければ 1,040 + 65,040 > 65,536 でキュー溢れになる。どちらでも全体はサーバー側 Block で破棄だが、
        // 合成は送信側の破棄理由を優先するため、キュー溢れの計上有無で予約戻しを直接確認できる
        var third = new FakeToken(PacketParserTests.IPv4(IpProtocol.Tcp, [127, 0, 0, 1], [127, 0, 0, 1], 40000, 8080, 65_000), new PacketMeta(0, true, true, false));
        f.Engine.Process(third);
        Assert.Equal(0, f.Engine.Statistics.DroppedQueueOverflow);
        Assert.Equal(3, f.Engine.Statistics.DroppedBlock);
        f.Engine.Dispose();
    }

    [Fact]
    public void DelayedPacket_IsReleasedByShaper_WhenDue()
    {
        var f = Create();
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Local, 50000, Remote, 443), 100, 0);
        f.Store.SetPidPolicy(100, new ControlPolicy { Upload = BandwidthLimit.FromBytesPerSecond(1_000) });
        f.Engine.Process(Outbound(50000, 443, payload: 65_000));
        var delayed = Outbound(50000, 443, payload: 1_000);
        f.Engine.Process(delayed);
        Assert.Equal(1, f.Engine.QueuedPackets);

        f.Time.Advance(TimeSpan.FromSeconds(10)); // 期限を過ぎた状態でシェーパを起動
        f.Engine.Start();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (f.Channel.Sent.Count < 2 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        Assert.Contains(delayed, f.Channel.Sent);
        Assert.Equal(0, f.Engine.QueuedPackets);
        Assert.Equal(65_040 + 1_040, f.Engine.Statistics.ReinjectedBytes);
        f.Engine.Dispose();
    }

    [Fact]
    public void PidTarget_MatchesByCreationTime_OrFallsBackToPidOnly()
    {
        // 自プロセスの PID は実在するので開始時刻が取れる。控えた開始時刻と一致すれば対象、ずれていれば対象外
        var time = new FakeTimeProvider();
        var store = new ControlPolicyStore(new ProcessNameResolver(NullLogger.Instance, time), time);
        uint self = (uint)Environment.ProcessId;
        store.SetPidPolicy(self, new ControlPolicy { Block = true });
        Assert.True(store.IsConfiguredTarget(self));

        // 存在しない PID を指定すると開始時刻は 0（取得不能）で、PID のみの照合にフォールバックする
        store.SetPidPolicy(0xFFFF_FFF0, new ControlPolicy { Block = true });
        Assert.True(store.IsConfiguredTarget(0xFFFF_FFF0));
    }

    private static FakeToken TcpOutbound(ushort srcPort, ushort dstPort, uint seq, uint ack, byte flags, int payload)
    {
        var p = PacketParserTests.IPv4(IpProtocol.Tcp, [192, 168, 1, 10], [1, 1, 1, 1], srcPort, dstPort, payload);
        var tcp = p.AsSpan(20);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tcp[4..], seq);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(tcp[8..], ack);
        tcp[12] = 0x50;
        tcp[13] = flags;
        return new FakeToken(p, new PacketMeta(0, Outbound: true, Loopback: false, IsIPv6: false));
    }

    [Fact]
    public void ResetTarget_InjectsRst_AndDropsOriginal()
    {
        var f = Create();
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Local, 50000, Remote, 443), 100, 0);
        f.Store.SetPidPolicy(100, new ControlPolicy { Reset = true });

        var token = TcpOutbound(50000, 443, seq: 1000, ack: 2000, flags: 0x10, payload: 0);
        f.Engine.Process(token);

        Assert.Single(f.Channel.Injected);
        Assert.Same(token, f.Channel.Released.Single());
        Assert.Empty(f.Channel.Sent);
        Assert.Equal(1, f.Engine.Statistics.ResetInjected);
        f.Engine.Dispose();
    }

    [Fact]
    public void ResetTarget_ThrottlesRepeatInjectionsPerConnection()
    {
        var f = Create();
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Local, 50000, Remote, 443), 100, 0);
        f.Store.SetPidPolicy(100, new ControlPolicy { Reset = true });

        for (int i = 0; i < 5; i++)
        {
            f.Engine.Process(TcpOutbound(50000, 443, seq: (uint)(1000 + i), ack: 2000, flags: 0x10, payload: 0));
        }

        Assert.Single(f.Channel.Injected);                    // 1 秒に 1 回だけ注入
        Assert.Equal(1, f.Engine.Statistics.ResetInjected);
        Assert.Equal(4, f.Engine.Statistics.ResetSuppressed);
        Assert.Equal(5, f.Channel.Released.Count);            // 元パケットはすべて破棄

        f.Time.Advance(TimeSpan.FromSeconds(1.1));
        f.Engine.Process(TcpOutbound(50000, 443, seq: 2000, ack: 2000, flags: 0x10, payload: 0));
        Assert.Equal(2, f.Channel.Injected.Count);            // 間隔を空ければ再度注入
        f.Engine.Dispose();
    }

    [Fact]
    public void ResetTarget_Udp_IsPassed_NotReset()
    {
        var f = Create();
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Udp, Local, 50000, Remote, 443), 100, 0);
        f.Store.SetPidPolicy(100, new ControlPolicy { Reset = true });

        var udp = new FakeToken(PacketParserTests.IPv4(IpProtocol.Udp, [192, 168, 1, 10], [1, 1, 1, 1], 50000, 443, 10), new PacketMeta(0, true, false, false));
        f.Engine.Process(udp);

        Assert.Empty(f.Channel.Injected);
        Assert.Same(udp, f.Channel.Sent.Single());
        f.Engine.Dispose();
    }

    [Fact]
    public void DroppedByProbability_IsReleased_NotSent()
    {
        var f = Create();
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Local, 50000, Remote, 443), 100, 0);
        f.Store.SetPidPolicy(100, new ControlPolicy { DropProbability = 1.0 });

        f.Engine.Process(Outbound(50000, 443));

        Assert.Single(f.Channel.Released);
        Assert.Empty(f.Channel.Sent);
        Assert.Equal(1, f.Engine.Statistics.DroppedRandom);
        f.Engine.Dispose();
    }

    [Fact]
    public void Dispose_IsIdempotent_AndShutsDownChannelOnce()
    {
        var f = Create();
        f.Engine.Start();
        f.Engine.Dispose();
        f.Engine.Dispose();

        Assert.Equal(1, f.Channel.ShutdownCalls);
        Assert.Equal(1, f.Channel.DisposeCalls);
    }

    [Fact]
    public void Threaded_TargetBlocked_OthersPass()
    {
        var f = Create();
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Local, 50000, Remote, 443), 100, 0);
        f.Table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Local, 50001, Remote, 443), 200, 0);
        f.Store.SetPidPolicy(100, new ControlPolicy { Block = true });
        f.Engine.Start();

        for (int i = 0; i < 50; i++)
        {
            f.Channel.Feed(Outbound(50000, 443));
            f.Channel.Feed(Outbound(50001, 443));
        }

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (f.Channel.Sent.Count + f.Channel.Released.Count < 100 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        f.Engine.Dispose();
        Assert.Equal(50, f.Channel.Released.Count);
        Assert.Equal(50, f.Channel.Sent.Count);
    }
}
