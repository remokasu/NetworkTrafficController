using NetworkTrafficController.Core.Control;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Control;

public class BandwidthLimitTests
{
    [Theory]
    [InlineData("100KB", 100_000)]
    [InlineData("2MB", 2_000_000)]
    [InlineData("8Mbps", 1_000_000)]
    [InlineData("1.5kb", 1_500)]
    [InlineData(" 10 MB ", 10_000_000)]
    public void Parse_Units_UseDecimalKilo(string text, double expectedBytesPerSecond)
    {
        var limit = BandwidthLimit.Parse(text);
        Assert.True(limit.HasLimit);
        Assert.False(limit.IsStopped);
        Assert.Equal(expectedBytesPerSecond, limit.BytesPerSecond, 3);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("stop")]
    [InlineData("0KB")]
    public void Zero_MeansStopped_NotUnlimited(string text)
    {
        var limit = BandwidthLimit.Parse(text);
        Assert.True(limit.HasLimit);
        Assert.True(limit.IsStopped);
    }

    [Theory]
    [InlineData("unlimited")]
    [InlineData("NONE")]
    public void Unlimited_HasNoLimit(string text)
    {
        var limit = BandwidthLimit.Parse(text);
        Assert.False(limit.HasLimit);
        Assert.False(limit.IsStopped);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("100")]
    [InlineData("-5KB")]
    [InlineData("xMB")]
    public void Invalid_Throws(string text)
    {
        Assert.Throws<FormatException>(() => BandwidthLimit.Parse(text));
    }

    [Fact]
    public void Mbps_IsDividedByEight()
    {
        Assert.Equal(125_000, BandwidthLimit.BytesPerUnit(BandwidthUnit.MegabitsPerSecond));
    }

    [Fact]
    public void FromBytesPerSecond_Zero_IsStopped()
    {
        Assert.True(BandwidthLimit.FromBytesPerSecond(0).IsStopped);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(999)]              // 1 KB/s 未満
    [InlineData(10_000_000_001)]  // 10 GB/s 超
    public void FromBytesPerSecond_OutOfRange_Throws(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BandwidthLimit.FromBytesPerSecond(value));
    }

    [Theory]
    [InlineData("1e308MB")]   // 掛け算で Infinity になる → 以前は「停止」に化けていた
    [InlineData("1e-12KB")]   // 極小 → 以前は待ち時間が桁あふれして制限が素通りになっていた
    [InlineData("0.5KB")]
    public void Parse_OutOfRange_Throws(string text)
    {
        Assert.Throws<FormatException>(() => BandwidthLimit.Parse(text));
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000_000_000)]
    public void FromBytesPerSecond_Boundaries_AreAccepted(double value)
    {
        Assert.Equal(value, BandwidthLimit.FromBytesPerSecond(value).BytesPerSecond);
    }
}
