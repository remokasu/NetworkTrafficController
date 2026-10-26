using NetworkTrafficController.Core.Monitoring;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Monitoring;

public class SpeedHistorySumTests
{
    private static SpeedHistory Of(int capacity, params (double Out, double In)[] points)
    {
        var h = new SpeedHistory(capacity);
        foreach (var (o, i) in points)
        {
            h.Add(o, i);
        }

        return h;
    }

    [Fact]
    public void SameLength_AddsPointByPoint()
    {
        var a = Of(4, (1, 10), (2, 20), (3, 30));
        var b = Of(4, (100, 1000), (200, 2000), (300, 3000));
        var o = new double[4];
        var i = new double[4];

        int n = SpeedHistory.SumLatest([a, b], o, i);

        Assert.Equal(3, n);
        Assert.Equal([101, 202, 303], o[..n]);
        Assert.Equal([1010, 2020, 3030], i[..n]);
    }

    [Fact]
    public void ShorterHistory_IsAlignedToTheLatestPoint()
    {
        // b は後から現れたプロセス。最新の 2 点だけを、a の最新 2 点に足す
        var a = Of(4, (1, 0), (2, 0), (3, 0), (4, 0));
        var b = Of(4, (10, 0), (20, 0));
        var o = new double[4];
        var i = new double[4];

        int n = SpeedHistory.SumLatest([a, b], o, i);

        Assert.Equal(4, n);
        Assert.Equal([1, 2, 13, 24], o);
    }

    [Fact]
    public void WrappedRingBuffer_UsesTheNewestPoints()
    {
        var a = Of(3, (1, 0), (2, 0), (3, 0), (4, 0), (5, 0));
        var o = new double[3];
        var i = new double[3];

        int n = SpeedHistory.SumLatest([a], o, i);

        Assert.Equal(3, n);
        Assert.Equal([3, 4, 5], o);
    }

    [Fact]
    public void NoHistories_WritesNothing()
    {
        var o = new double[3];
        var i = new double[3];
        Assert.Equal(0, SpeedHistory.SumLatest([], o, i));
    }
}
