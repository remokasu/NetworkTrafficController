using NetworkTrafficController.Core.Control;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Control;

public class ControlPanelMixTests
{
    private static readonly ControlPanelState Base = new()
    {
        DropPercent = "10",
        DelayMs = "100",
        JitterMs = "0",
        Upload = "1",
        UploadUnit = BandwidthUnit.MegabytesPerSecond,
        Download = "1",
        DownloadUnit = BandwidthUnit.MegabytesPerSecond,
    };

    [Fact]
    public void Combine_SameValues_AreKept()
    {
        var mix = ControlPanelMix.Combine([Base with { Block = true }, Base with { Block = true }]);

        Assert.True(mix.Block);
        Assert.False(mix.Reset);
        Assert.Equal("100", mix.DelayMs);
        Assert.Equal(BandwidthUnit.MegabytesPerSecond, mix.UploadUnit);
        Assert.True(mix.BlockOut);
        Assert.True(mix.BlockIn);
    }

    // SPEC-0002: 向きのトグルも項目ごとに混在する
    [Fact]
    public void Combine_DifferentDirections_BecomeMixed_AndApplyToKeepsOwn()
    {
        var a = Base with { Block = true, BlockOut = true, BlockIn = false };
        var b = Base with { Block = true, BlockOut = true, BlockIn = true };
        var mix = ControlPanelMix.Combine([a, b]);

        Assert.True(mix.BlockOut);
        Assert.Null(mix.BlockIn);

        // 触っていない ↓ は各自の値が残る
        Assert.False(mix.ApplyTo(a).BlockIn);
        Assert.True(mix.ApplyTo(b).BlockIn);
    }

    [Fact]
    public void Combine_DifferentValues_BecomeMixed()
    {
        var mix = ControlPanelMix.Combine([
            Base with { Block = true, DelayMs = "100", UploadUnit = BandwidthUnit.KilobytesPerSecond },
            Base with { Block = false, DelayMs = "200" },
        ]);

        Assert.Null(mix.Block);
        Assert.Null(mix.DelayMs);
        Assert.Null(mix.UploadUnit);
        Assert.Equal("1", mix.Upload);
    }

    [Fact]
    public void ApplyTo_OverridesOnlyDecidedItems()
    {
        // Block は混在のまま触っていない（null）。Delay だけを ON にした
        var mix = new ControlPanelMix { DelayOn = true, DelayMs = "250" };
        var own = Base with { Block = true, UploadOn = true, Upload = "300", UploadUnit = BandwidthUnit.KilobytesPerSecond };

        var applied = mix.ApplyTo(own);

        Assert.True(applied.Block);
        Assert.True(applied.UploadOn);
        Assert.Equal("300", applied.Upload);
        Assert.Equal(BandwidthUnit.KilobytesPerSecond, applied.UploadUnit);
        Assert.True(applied.DelayOn);
        Assert.Equal("250", applied.DelayMs);
    }

    [Fact]
    public void CombineThenApply_WithoutChanges_KeepsEachOwnState()
    {
        var a = Base with { Block = true, DelayOn = true, DelayMs = "100" };
        var b = Base with { Reset = true, DelayOn = true, DelayMs = "300" };
        var mix = ControlPanelMix.Combine([a, b]);

        Assert.Equal(a, mix.ApplyTo(a));
        Assert.Equal(b, mix.ApplyTo(b));
    }

    [Fact]
    public void ApplyTo_DecidedCheck_SetsItForEveryone()
    {
        var a = Base with { Block = true };
        var b = Base with { Block = false };
        var mix = ControlPanelMix.Combine([a, b]) with { Block = true };

        Assert.True(mix.ApplyTo(a).Block);
        Assert.True(mix.ApplyTo(b).Block);
    }
}
