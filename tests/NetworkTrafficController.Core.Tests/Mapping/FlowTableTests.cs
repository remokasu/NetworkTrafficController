using Microsoft.Extensions.Logging.Abstractions;
using NetworkTrafficController.Core.Mapping;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Mapping;

public class FlowTableTests
{
    private static readonly NetAddress Local = NetAddress.FromIPv4Bytes([192, 168, 1, 10]);
    private static readonly NetAddress Remote = NetAddress.FromIPv4Bytes([93, 184, 216, 34]);
    private static readonly NetAddress Any = NetAddress.FromIPv4Bytes([0, 0, 0, 0]);
    private static readonly FlowKey Key = new(IpProtocol.Tcp, Local, 50000, Remote, 443);

    private static (FlowTable Table, FakeTimeProvider Time) Create(FlowTableOptions? options = null)
    {
        var time = new FakeTimeProvider();
        return (new FlowTable(options ?? new FlowTableOptions(), time, NullLogger.Instance), time);
    }

    [Fact]
    public void TryResolve_Miss_WhenEmpty()
    {
        var (table, _) = Create();
        Assert.False(table.TryResolve(Key, out _, out var source));
        Assert.Equal(ResolveSource.None, source);
    }

    [Fact]
    public void FlowEstablished_ThenResolve_ReturnsExact()
    {
        var (table, _) = Create();
        table.OnFlowEstablished(Key, 1234, 99);

        Assert.True(table.TryResolve(Key, out uint pid, out var source));
        Assert.Equal(1234u, pid);
        Assert.Equal(ResolveSource.Exact, source);
    }

    [Fact]
    public void Resolve_FallsBackToEndpoint_ThenWildcard()
    {
        var (table, _) = Create();
        var listen = new TcpConnectionRow(new FlowKey(IpProtocol.Tcp, Local, 8080, Any, 0), 10, TcpState.Listen);
        var udpAny = new UdpEndpointRow(new EndpointKey(IpProtocol.Udp, Any, 5353), 20);
        table.ApplySnapshot([listen], [udpAny]);

        Assert.True(table.TryResolve(new FlowKey(IpProtocol.Tcp, Local, 8080, Remote, 1), out uint pid1, out var s1));
        Assert.Equal(10u, pid1);
        Assert.Equal(ResolveSource.Endpoint, s1);

        Assert.True(table.TryResolve(new FlowKey(IpProtocol.Udp, Local, 5353, Remote, 5353), out uint pid2, out var s2));
        Assert.Equal(20u, pid2);
        Assert.Equal(ResolveSource.Wildcard, s2);

        Assert.False(table.TryResolve(new FlowKey(IpProtocol.Udp, Local, 5354, Remote, 5353), out _, out _));
    }

    [Fact]
    public void Snapshot_DoesNotOverride_FlowEventEntry_ButCountsConflict()
    {
        var (table, _) = Create();
        table.OnFlowEstablished(Key, 1, 0);
        table.ApplySnapshot([new TcpConnectionRow(Key, 2, TcpState.Established)], []);

        Assert.True(table.TryResolve(Key, out uint pid, out _));
        Assert.Equal(1u, pid);
        Assert.Equal(1, table.ConflictCount);
    }

    [Fact]
    public void FlowEvent_Overrides_SnapshotEntry()
    {
        var (table, _) = Create();
        table.ApplySnapshot([new TcpConnectionRow(Key, 2, TcpState.Established)], []);
        table.OnFlowEstablished(Key, 3, 0);

        Assert.True(table.TryResolve(Key, out uint pid, out _));
        Assert.Equal(3u, pid);
    }

    [Fact]
    public void Deleted_StaysResolvable_DuringGrace_ThenSwept()
    {
        var (table, time) = Create(new FlowTableOptions { GracePeriod = TimeSpan.FromSeconds(10) });
        table.OnFlowEstablished(Key, 5, 0);
        table.OnFlowDeleted(Key);

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, table.Sweep());
        Assert.True(table.TryResolve(Key, out _, out _));

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(1, table.Sweep());
        Assert.False(table.TryResolve(Key, out _, out _));
    }

    [Fact]
    public void SnapshotEntry_VanishedFromNextSnapshot_IsSweptAfterGrace()
    {
        var (table, time) = Create(new FlowTableOptions { GracePeriod = TimeSpan.FromSeconds(1) });
        table.ApplySnapshot([new TcpConnectionRow(Key, 2, TcpState.Established)], []);
        table.ApplySnapshot([], []);
        Assert.True(table.TryResolve(Key, out _, out _));

        time.Advance(TimeSpan.FromSeconds(2));
        table.Sweep();
        Assert.False(table.TryResolve(Key, out _, out _));
    }

    [Fact]
    public void ReEstablished_AfterDelete_ClearsDeletion()
    {
        var (table, time) = Create(new FlowTableOptions { GracePeriod = TimeSpan.FromSeconds(1) });
        table.OnFlowEstablished(Key, 5, 0);
        table.OnFlowDeleted(Key);
        table.OnFlowEstablished(Key, 6, 1);
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, table.Sweep());
        Assert.True(table.TryResolve(Key, out uint pid, out _));
        Assert.Equal(6u, pid);
    }

    [Fact]
    public void TryCollectLocalPorts_NoTargets_ReturnsEmptyAndTrue()
    {
        var (table, _) = Create();
        table.OnFlowEstablished(Key, 1, 0);

        Assert.True(table.TryCollectLocalPorts(_ => false, 64, out var tcp, out var udp));
        Assert.Empty(tcp);
        Assert.Empty(udp);
    }

    [Fact]
    public void TryCollectLocalPorts_GathersFromExactEndpointAndWildcard_SplitByProtocol()
    {
        var (table, _) = Create();
        table.OnFlowEstablished(Key, 7, 0);                                                         // TCP 50000（Exact）
        table.OnFlowEstablished(new FlowKey(IpProtocol.Tcp, Local, 50001, Remote, 443), 8, 0);       // 対象外
        table.ApplySnapshot(
            [new TcpConnectionRow(new FlowKey(IpProtocol.Tcp, Local, 8080, Any, 0), 7, TcpState.Listen)], // TCP 8080（Endpoint）
            [new UdpEndpointRow(new EndpointKey(IpProtocol.Udp, Any, 5353), 7)]);                         // UDP 5353（Wildcard）

        Assert.True(table.TryCollectLocalPorts(pid => pid == 7, 64, out var tcp, out var udp));
        Assert.Equal([8080, 50000], tcp.Order().ToArray());
        Assert.Equal([(ushort)5353], udp.ToArray());
    }

    [Fact]
    public void TryCollectLocalPorts_IncludesEntriesInDeletionGrace()
    {
        var (table, _) = Create();
        table.OnFlowEstablished(Key, 7, 0);
        table.OnFlowDeleted(Key);

        Assert.True(table.TryCollectLocalPorts(pid => pid == 7, 64, out var tcp, out _));
        Assert.Equal([(ushort)50000], tcp.ToArray());
    }

    [Fact]
    public void TryCollectLocalPorts_DeduplicatesAndRespectsMax()
    {
        var (table, _) = Create();
        table.OnFlowEstablished(Key with { RemotePort = 1 }, 7, 0);   // 同じローカルポート 50000
        table.OnFlowEstablished(Key with { RemotePort = 2 }, 7, 0);
        table.OnFlowEstablished(Key with { LocalPort = 50002 }, 7, 0);

        Assert.True(table.TryCollectLocalPorts(pid => pid == 7, 2, out var tcp, out _));   // ちょうど上限
        Assert.Equal(2, tcp.Count);
        Assert.False(table.TryCollectLocalPorts(pid => pid == 7, 1, out _, out _));         // 上限超過
    }

    [Fact]
    public void MaxEntries_EvictsDeletedFirst()
    {
        var (table, _) = Create(new FlowTableOptions { MaxEntries = 2 });
        var k1 = Key with { LocalPort = 1 };
        var k2 = Key with { LocalPort = 2 };
        var k3 = Key with { LocalPort = 3 };
        table.OnFlowEstablished(k1, 1, 0);
        table.OnFlowEstablished(k2, 2, 0);
        table.OnFlowDeleted(k1);
        table.OnFlowEstablished(k3, 3, 0);

        Assert.Equal(2, table.ExactCount);
        Assert.False(table.TryResolve(k1, out _, out _));
        Assert.True(table.TryResolve(k2, out _, out _));
        Assert.True(table.TryResolve(k3, out _, out _));
    }
}
