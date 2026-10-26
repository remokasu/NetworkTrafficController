using NetworkTrafficController.Core.Mapping;
using NetworkTrafficController.Core.Monitoring;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Monitoring;

public class MonitorAggregatorTests
{
    private static readonly NetAddress A = NetAddress.FromIPv4Bytes([10, 0, 0, 1]);
    private static readonly NetAddress B = NetAddress.FromIPv4Bytes([10, 0, 0, 2]);

    private static FlowKey Key(ushort port) => new(IpProtocol.Tcp, A, port, B, 80);

    [Fact]
    public void Expire_RemovesIdleConnections_ThenIdleProcesses()
    {
        var time = new FakeTimeProvider();
        var agg = new MonitorAggregator(new MonitorAggregatorOptions(), time);
        agg.Attribute(1, Key(1), Direction.Outbound, 10, false);

        time.Advance(TimeSpan.FromSeconds(30));
        agg.Attribute(2, Key(2), Direction.Outbound, 10, false);

        time.Advance(TimeSpan.FromSeconds(15)); // pid1 idle 45s, pid2 idle 15s
        agg.Expire(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(40));

        var s = agg.Snapshot();
        Assert.Single(s.Connections);
        Assert.Equal(2u, s.Connections[0].Pid);
        Assert.Single(s.Processes);
        Assert.Equal(2u, s.Processes[0].Pid);
    }

    [Fact]
    public void MaxConnections_EvictsLeastRecentlySeen()
    {
        var time = new FakeTimeProvider();
        var agg = new MonitorAggregator(new MonitorAggregatorOptions { MaxConnections = 2 }, time);
        agg.Attribute(1, Key(1), Direction.Outbound, 1, false);
        time.Advance(TimeSpan.FromSeconds(1));
        agg.Attribute(1, Key(2), Direction.Outbound, 1, false);
        time.Advance(TimeSpan.FromSeconds(1));
        agg.Attribute(1, Key(3), Direction.Outbound, 1, false);

        var s = agg.Snapshot();
        Assert.Equal(2, s.Connections.Count);
        Assert.DoesNotContain(s.Connections, c => c.Key.LocalPort == 1);
        Assert.Equal(2, s.Processes[0].ConnectionCount);
    }

    [Fact]
    public void MaxConnections_EvictsInBatches_SoInsertStaysCheap()
    {
        var time = new FakeTimeProvider();
        var agg = new MonitorAggregator(new MonitorAggregatorOptions { MaxConnections = 20 }, time);
        for (ushort port = 1; port <= 21; port++)
        {
            agg.Attribute(1, Key(port), Direction.Outbound, 1, false);
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        // 20 件到達後の 21 件目で 10%（2 件）が追い出され、新規 1 件が入る → 19 件
        Assert.Equal(19, agg.Snapshot().Connections.Count);
    }
}
