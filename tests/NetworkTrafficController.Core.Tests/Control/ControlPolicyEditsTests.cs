using NetworkTrafficController.Core.Control;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Control;

public class ControlPolicyEditsTests
{
    private static ControlPolicy? Build(ControlPanelState s, ControlPolicy? current, out List<string> errors)
    {
        errors = new List<string>();
        return ControlPolicyEdits.FromPanel(current, s, errors);
    }

    [Fact]
    public void BlockOnly_CreatesBlockPolicy()
    {
        var p = Build(new ControlPanelState { Block = true }, null, out var errors);
        Assert.Empty(errors);
        Assert.True(p!.Block);
        Assert.False(p.HasDelay);
        Assert.False(p.Upload.HasLimit);
    }

    [Fact]
    public void NothingOn_ReturnsNull()
    {
        Assert.Null(Build(new ControlPanelState { DelayMs = "100", Upload = "5" }, null, out _)); // 値があっても OFF なら無効
    }

    [Theory]
    [InlineData(BandwidthUnit.KilobytesPerSecond, "200", 200_000)]
    [InlineData(BandwidthUnit.MegabytesPerSecond, "2", 2_000_000)]
    [InlineData(BandwidthUnit.MegabitsPerSecond, "8", 1_000_000)]
    [InlineData(BandwidthUnit.KilobytesPerSecond, "1.5", 1_500)]
    public void Upload_ValueAndUnit(BandwidthUnit unit, string value, double expected)
    {
        var p = Build(new ControlPanelState { UploadOn = true, Upload = value, UploadUnit = unit }, null, out var errors);
        Assert.Empty(errors);
        Assert.Equal(expected, p!.Upload.BytesPerSecond, 3);
    }

    [Fact]
    public void Bandwidth_Zero_IsStopped()
    {
        var p = Build(new ControlPanelState { DownloadOn = true, Download = "0" }, null, out _);
        Assert.True(p!.Download.IsStopped);
    }

    [Fact]
    public void InvalidBandwidth_KeepsPreviousValue_ButOtherItemsApply()
    {
        var current = new ControlPolicy { Upload = BandwidthLimit.FromBytesPerSecond(300_000) };
        var p = Build(new ControlPanelState { Block = true, UploadOn = true, Upload = "abc" }, current, out var errors);

        Assert.True(p!.Block);                              // Block は反映される
        Assert.Equal(300_000, p.Upload.BytesPerSecond, 3);  // 上り帯域は前の設定のまま
        Assert.Single(errors);
        Assert.Contains("Upload bandwidth", errors[0]);
    }

    [Fact]
    public void InvalidBandwidth_WithoutPrevious_IsTreatedAsOff()
    {
        var p = Build(new ControlPanelState { Block = true, UploadOn = true, Upload = "" }, null, out var errors);
        Assert.False(p!.Upload.HasLimit);
        Assert.Single(errors);
    }

    [Theory]
    [InlineData("0.5")]      // 1 KB/s 未満
    [InlineData("-1")]
    public void OutOfRangeBandwidth_IsError(string value)
    {
        Build(new ControlPanelState { UploadOn = true, Upload = value }, null, out var errors);
        Assert.Single(errors);
    }

    [Fact]
    public void Delay_WithEmptyJitter_IsZeroJitter()
    {
        var p = Build(new ControlPanelState { DelayOn = true, DelayMs = "150", JitterMs = "" }, null, out var errors);
        Assert.Empty(errors);
        Assert.Equal(150, p!.Delay.TotalMilliseconds);
        Assert.Equal(TimeSpan.Zero, p.Jitter);
    }

    [Theory]
    [InlineData("150", "abc")]
    [InlineData("20000", "0")]  // 上限 10000 ms 超
    public void InvalidDelay_KeepsPrevious(string delay, string jitter)
    {
        var current = new ControlPolicy { Delay = TimeSpan.FromMilliseconds(50) };
        var p = Build(new ControlPanelState { DelayOn = true, DelayMs = delay, JitterMs = jitter }, current, out var errors);
        Assert.Equal(50, p!.Delay.TotalMilliseconds);
        Assert.Single(errors);
    }

    [Theory]
    [InlineData("30", 0.30)]
    [InlineData("100", 1.0)]
    public void Drop_Percent(string text, double expected)
    {
        var p = Build(new ControlPanelState { DropOn = true, DropPercent = text }, null, out _);
        Assert.Equal(expected, p!.DropProbability, 6);
    }

    [Fact]
    public void Drop_OutOfRange_IsError()
    {
        Build(new ControlPanelState { DropOn = true, DropPercent = "150" }, null, out var errors);
        Assert.Single(errors);
    }

    [Fact]
    public void Off_ClearsPreviousSetting()
    {
        var current = new ControlPolicy { Block = true, Delay = TimeSpan.FromMilliseconds(50) };
        var p = Build(new ControlPanelState { Block = true, DelayOn = false, DelayMs = "50" }, current, out _);
        Assert.False(p!.HasDelay); // チェックを外した項目は消える
    }

    [Fact]
    public void ToPanel_RoundTrips()
    {
        var policy = new ControlPolicy
        {
            Block = true,
            DropProbability = 0.25,
            Delay = TimeSpan.FromMilliseconds(120),
            Jitter = TimeSpan.FromMilliseconds(30),
            Upload = BandwidthLimit.FromBytesPerSecond(2_000_000),
            Download = BandwidthLimit.FromBytesPerSecond(200_000),
            BlockDirection = ControlDirection.Inbound,
            DropDirection = ControlDirection.Outbound,
            DelayDirection = ControlDirection.Inbound,
        };
        var state = ControlPolicyEdits.ToPanel(policy, new ControlPanelState());
        Assert.Equal("2", state.Upload);
        Assert.Equal(BandwidthUnit.MegabytesPerSecond, state.UploadUnit);
        Assert.Equal("200", state.Download);
        Assert.Equal(BandwidthUnit.KilobytesPerSecond, state.DownloadUnit);
        Assert.False(state.BlockOut);
        Assert.True(state.BlockIn);
        Assert.True(state.DropOut);
        Assert.False(state.DropIn);
        Assert.True(state.ResetOut);
        Assert.True(state.ResetIn);

        var back = Build(state, null, out var errors);
        Assert.Empty(errors);
        Assert.Equal(policy, back);
    }

    // SPEC-0002: 両方 OFF は無効。前の向きを残して理由を返す
    [Fact]
    public void FromPanel_BothDirectionsOff_KeepsPreviousAndReportsError()
    {
        var current = new ControlPolicy { Block = true, BlockDirection = ControlDirection.Inbound };
        var state = new ControlPanelState { Block = true, BlockOut = false, BlockIn = false };

        var next = Build(state, current, out var errors);

        Assert.NotNull(next);
        Assert.Equal(ControlDirection.Inbound, next!.BlockDirection);
        Assert.Contains(errors, e => e.StartsWith("Block direction:", StringComparison.Ordinal));
    }

    // SPEC-0002 U3: 項目が OFF でも向きはポリシーに残る（他の項目で制御が有効なとき）
    [Fact]
    public void FromPanel_DirectionIsKept_WhenItemIsOff()
    {
        var state = new ControlPanelState { Block = true, DropOn = false, DropOut = true, DropIn = false };

        var next = Build(state, null, out var errors);

        Assert.Empty(errors);
        Assert.Equal(0, next!.DropProbability);
        Assert.Equal(ControlDirection.Outbound, next.DropDirection);
    }
}
