using NetworkTrafficController.Core.Mapping;
using NetworkTrafficController.Core.Monitoring;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Monitoring;

public class RateCalculatorTests
{
    private static readonly FlowKey Key = new(IpProtocol.Tcp, NetAddress.FromIPv4Bytes([10, 0, 0, 1]), 1, NetAddress.FromIPv4Bytes([10, 0, 0, 2]), 80);

    [Fact]
    public void FirstUpdate_ReturnsZeroRates()
    {
        var time = new FakeTimeProvider();
        var agg = new MonitorAggregator(new MonitorAggregatorOptions(), time);
        agg.Attribute(7, Key, Direction.Outbound, 1000, false);

        var first = new RateCalculator(time).Update(agg.Snapshot());

        Assert.Equal(0, first.TotalOutBps);
        var p = Assert.Single(first.Processes);
        Assert.Equal(0, p.OutBytesPerSecond);
        Assert.Equal(1000, p.Total.OutBytes);
    }

    [Fact]
    public void Update_ComputesBytesPerSecondFromDeltas()
    {
        var time = new FakeTimeProvider();
        var agg = new MonitorAggregator(new MonitorAggregatorOptions(), time);
        var rates = new RateCalculator(time);

        agg.RecordPacket(Direction.Outbound, 1000, false);
        agg.Attribute(7, Key, Direction.Outbound, 1000, false);
        rates.Update(agg.Snapshot());

        time.Advance(TimeSpan.FromSeconds(2));
        agg.RecordPacket(Direction.Inbound, 4000, false);
        agg.Attribute(7, Key, Direction.Inbound, 4000, false);
        var second = rates.Update(agg.Snapshot());

        Assert.Equal(0, second.TotalOutBps);
        Assert.Equal(2000, second.TotalInBps, 3);
        var p = Assert.Single(second.Processes);
        Assert.Equal(2000, p.InBytesPerSecond, 3);
        Assert.Equal(0, p.OutBytesPerSecond, 3);
        Assert.Equal(5000, p.Total.TotalBytes);
    }

    [Fact]
    public void NewProcess_SinceLastSnapshot_UsesFullCountersAsDelta()
    {
        var time = new FakeTimeProvider();
        var agg = new MonitorAggregator(new MonitorAggregatorOptions(), time);
        var rates = new RateCalculator(time);
        rates.Update(agg.Snapshot());

        time.Advance(TimeSpan.FromSeconds(1));
        agg.Attribute(9, Key, Direction.Outbound, 500, false);
        var r = rates.Update(agg.Snapshot());

        Assert.Equal(500, Assert.Single(r.Processes).OutBytesPerSecond, 3);
    }
}
