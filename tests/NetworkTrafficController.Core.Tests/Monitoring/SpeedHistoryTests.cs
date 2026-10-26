using NetworkTrafficController.Core.Monitoring;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Monitoring;

public class SpeedHistoryTests
{
    [Fact]
    public void Empty_CopiesNothing()
    {
        var h = new SpeedHistory(4);
        Assert.Equal(0, h.Count);
        Span<double> buf = stackalloc double[4];
        h.CopyOut(buf);
        Assert.Equal(0, h.Count);
    }

    [Fact]
    public void FillsInOrder_UpToCapacity()
    {
        var h = new SpeedHistory(3);
        h.Add(1, 10);
        h.Add(2, 20);
        Assert.Equal(2, h.Count);

        Span<double> outb = stackalloc double[2];
        Span<double> inb = stackalloc double[2];
        h.CopyOut(outb);
        h.CopyIn(inb);
        Assert.Equal([1, 2], outb.ToArray());
        Assert.Equal([10, 20], inb.ToArray());
    }

    [Fact]
    public void WrapsAround_KeepingMostRecentInOrder()
    {
        var h = new SpeedHistory(3);
        for (int i = 1; i <= 5; i++)
        {
            h.Add(i, i * 10);
        }

        Assert.Equal(3, h.Count);
        Span<double> outb = stackalloc double[3];
        h.CopyOut(outb);
        Assert.Equal([3, 4, 5], outb.ToArray()); // 古い 1,2 は押し出され、古い順に 3,4,5
    }

    [Fact]
    public void CopyInto_SmallerDestination_TakesWhatFits()
    {
        var h = new SpeedHistory(5);
        for (int i = 1; i <= 5; i++)
        {
            h.Add(i, 0);
        }

        Span<double> dest = stackalloc double[3];
        h.CopyOut(dest);
        Assert.Equal([1, 2, 3], dest.ToArray());
    }

    [Fact]
    public void Clear_Resets()
    {
        var h = new SpeedHistory(3);
        h.Add(1, 1);
        h.Clear();
        Assert.Equal(0, h.Count);
        h.Add(9, 9);
        Span<double> outb = stackalloc double[1];
        h.CopyOut(outb);
        Assert.Equal(9, outb[0]);
    }
}
