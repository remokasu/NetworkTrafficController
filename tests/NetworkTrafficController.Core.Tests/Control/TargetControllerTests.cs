using NetworkTrafficController.Core.Control;
using NetworkTrafficController.Core.Mapping;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Control;

public class TargetControllerTests
{
    private static TargetController Create(ControlPolicy policy, Random? random = null) => new(policy, new FakeTimeProvider(), random);

    // 明示しない限り TCP として評価する
    private static PacketDisposition Eval(TargetController c, Direction d, int size, bool isTcp = true) => c.Evaluate(d, size, isTcp);

    [Fact]
    public void Block_DropsBothDirections_WhenDirectionBoth()
    {
        var c = Create(new ControlPolicy { Block = true });
        Assert.Equal(PacketActionKind.Drop, Eval(c, Direction.Outbound, 100).Action);
        var d = Eval(c, Direction.Inbound, 100);
        Assert.Equal(PacketActionKind.Drop, d.Action);
        Assert.Equal(DropReason.Block, d.DropReason);
    }

    [Fact]
    public void Block_OutboundOnly_PassesInbound()
    {
        var c = Create(new ControlPolicy { Block = true, BlockDirection = ControlDirection.Outbound });
        Assert.Equal(PacketActionKind.Drop, Eval(c, Direction.Outbound, 100).Action);
        Assert.Equal(PacketActionKind.Pass, Eval(c, Direction.Inbound, 100).Action);
    }

    [Fact]
    public void Block_InboundOnly_PassesOutbound()
    {
        var c = Create(new ControlPolicy { Block = true, BlockDirection = ControlDirection.Inbound });
        Assert.Equal(PacketActionKind.Pass, Eval(c, Direction.Outbound, 100).Action);
        Assert.Equal(PacketActionKind.Drop, Eval(c, Direction.Inbound, 100).Action);
    }

    // SPEC-0002: 向きは項目ごと。Reset / Drop / Delay も片方向にすると、もう片方は素通しになる
    [Fact]
    public void Reset_OutboundOnly_PassesInboundTcp()
    {
        var c = Create(new ControlPolicy { Reset = true, ResetDirection = ControlDirection.Outbound });
        Assert.Equal(PacketActionKind.Reset, Eval(c, Direction.Outbound, 100).Action);
        Assert.Equal(PacketActionKind.Pass, Eval(c, Direction.Inbound, 100).Action);
    }

    [Fact]
    public void Drop_InboundOnly_PassesOutbound()
    {
        var c = Create(new ControlPolicy { DropProbability = 1.0, DropDirection = ControlDirection.Inbound });
        Assert.Equal(PacketActionKind.Pass, Eval(c, Direction.Outbound, 100).Action);
        var d = Eval(c, Direction.Inbound, 100);
        Assert.Equal(PacketActionKind.Drop, d.Action);
        Assert.Equal(DropReason.Random, d.DropReason);
    }

    [Fact]
    public void Delay_OutboundOnly_PassesInbound()
    {
        var c = Create(new ControlPolicy { Delay = TimeSpan.FromMilliseconds(100), DelayDirection = ControlDirection.Outbound });
        Assert.Equal(PacketActionKind.Delay, Eval(c, Direction.Outbound, 100).Action);
        Assert.Equal(PacketActionKind.Pass, Eval(c, Direction.Inbound, 100).Action);
    }

    // SPEC-0002: 帯域は Upload / Download で向きが決まり、他の項目の向きの設定を見ない
    [Fact]
    public void Bandwidth_AppliesRegardlessOfOtherDirections()
    {
        var c = Create(new ControlPolicy
        {
            Block = true,
            BlockDirection = ControlDirection.Outbound,
            Download = BandwidthLimit.Stopped,
        });
        Assert.Equal(DropReason.Block, Eval(c, Direction.Outbound, 100).DropReason);
        var d = Eval(c, Direction.Inbound, 100);
        Assert.Equal(PacketActionKind.Drop, d.Action);
        Assert.Equal(DropReason.BandwidthStopped, d.DropReason);
    }

    // 片方向の項目が飛ばされても、後段（帯域 → Delay）は評価される
    [Fact]
    public void SkippedDirection_StillEvaluatesLaterStages()
    {
        var c = Create(new ControlPolicy
        {
            Block = true,
            BlockDirection = ControlDirection.Outbound,
            Delay = TimeSpan.FromMilliseconds(50),
        });
        Assert.Equal(PacketActionKind.Delay, Eval(c, Direction.Inbound, 100).Action);
    }

    [Theory]
    [InlineData(ControlDirection.None)]
    [InlineData((ControlDirection)4)]
    public void Validate_RejectsInvalidDirection(ControlDirection direction)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControlPolicy { BlockDirection = direction }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControlPolicy { ResetDirection = direction }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControlPolicy { DropDirection = direction }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControlPolicy { DelayDirection = direction }.Validate());
    }

    [Fact]
    public void Reset_OnTcp_ReturnsReset_ButPassesUdp()
    {
        var c = Create(new ControlPolicy { Reset = true });
        Assert.Equal(PacketActionKind.Reset, Eval(c, Direction.Outbound, 100, isTcp: true).Action);
        // UDP には RST が無いのでそのまま通す（SPEC 決定事項）
        Assert.Equal(PacketActionKind.Pass, Eval(c, Direction.Outbound, 100, isTcp: false).Action);
    }

    [Fact]
    public void Block_BeatsReset()
    {
        var c = Create(new ControlPolicy { Block = true, Reset = true });
        Assert.Equal(PacketActionKind.Drop, Eval(c, Direction.Outbound, 100).Action);
    }

    [Fact]
    public void Drop_WithProbabilityOne_AlwaysDrops_WithRandomReason()
    {
        var c = Create(new ControlPolicy { DropProbability = 1.0 });
        var d = Eval(c, Direction.Outbound, 100);
        Assert.Equal(PacketActionKind.Drop, d.Action);
        Assert.Equal(DropReason.Random, d.DropReason);
    }

    [Fact]
    public void Drop_WithProbabilityZero_NeverDrops()
    {
        var c = Create(new ControlPolicy { DropProbability = 0 });
        Assert.Equal(PacketActionKind.Pass, Eval(c, Direction.Outbound, 100).Action);
    }

    [Fact]
    public void Drop_Probability_ApproximatelyMatchesOverManyPackets()
    {
        var c = Create(new ControlPolicy { DropProbability = 0.3 }, new Random(12345));
        int dropped = 0;
        const int n = 10_000;
        for (int i = 0; i < n; i++)
        {
            if (Eval(c, Direction.Outbound, 100).Action == PacketActionKind.Drop)
            {
                dropped++;
            }
        }

        Assert.InRange(dropped / (double)n, 0.27, 0.33);
    }

    [Fact]
    public void Delay_TurnsPassIntoDelay_WithReleaseAtNowPlusDelay()
    {
        var time = new FakeTimeProvider();
        var c = new TargetController(new ControlPolicy { Delay = TimeSpan.FromMilliseconds(100) }, time);
        var d = c.Evaluate(Direction.Outbound, 100, isTcp: true);
        Assert.Equal(PacketActionKind.Delay, d.Action);
        Assert.False(d.Reserved); // 固定遅延だけなら帯域キューには計上しない
        long expected = time.GetTimestamp() + (long)(0.1 * time.TimestampFrequency);
        Assert.Equal(expected, d.ReleaseTimestamp);
    }

    [Fact]
    public void Delay_Jitter_StaysWithinBounds()
    {
        var time = new FakeTimeProvider();
        var c = new TargetController(new ControlPolicy { Delay = TimeSpan.FromMilliseconds(100), Jitter = TimeSpan.FromMilliseconds(20) }, time, new Random(7));
        long freq = time.TimestampFrequency;
        for (int i = 0; i < 1000; i++)
        {
            long release = c.Evaluate(Direction.Outbound, 100, isTcp: true).ReleaseTimestamp;
            double ms = (release - time.GetTimestamp()) / (double)freq * 1000;
            Assert.InRange(ms, 80, 120);
        }
    }

    [Fact]
    public void Delay_AddsOnTopOfBandwidthRelease()
    {
        var time = new FakeTimeProvider();
        var c = new TargetController(new ControlPolicy { Upload = BandwidthLimit.FromBytesPerSecond(1_000), Delay = TimeSpan.FromMilliseconds(100) }, time);
        c.Evaluate(Direction.Outbound, 65_000, isTcp: true); // バーストを使い切る
        var d = c.Evaluate(Direction.Outbound, 1_000, isTcp: true); // 帯域で遅延 + 固定遅延
        Assert.Equal(PacketActionKind.Delay, d.Action);
        Assert.True(d.Reserved); // 帯域キューに計上したので戻す必要がある
        long minimum = time.GetTimestamp() + (long)(0.1 * time.TimestampFrequency);
        Assert.True(d.ReleaseTimestamp > minimum);
    }

    [Fact]
    public void Stopped_DropsWithBandwidthStoppedReason()
    {
        var c = Create(new ControlPolicy { Download = BandwidthLimit.Stopped });
        var d = Eval(c, Direction.Inbound, 100);
        Assert.Equal(PacketActionKind.Drop, d.Action);
        Assert.Equal(DropReason.BandwidthStopped, d.DropReason);
        Assert.Equal(PacketActionKind.Pass, Eval(c, Direction.Outbound, 100).Action);
    }

    [Fact]
    public void Limited_PassesWithinBurst_ThenDelays()
    {
        var c = Create(new ControlPolicy { Upload = BandwidthLimit.FromBytesPerSecond(10_000) });
        Assert.Equal(PacketActionKind.Pass, Eval(c, Direction.Outbound, 60_000).Action);
        var d = Eval(c, Direction.Outbound, 10_000);
        Assert.Equal(PacketActionKind.Delay, d.Action);
        Assert.True(d.Reserved);
    }

    [Fact]
    public void Limited_QueueOverflow_DropsExcess()
    {
        var c = Create(new ControlPolicy { Upload = BandwidthLimit.FromBytesPerSecond(1_000) });
        Assert.Equal(PacketActionKind.Pass, Eval(c, Direction.Outbound, 65_000).Action);
        Assert.Equal(PacketActionKind.Delay, Eval(c, Direction.Outbound, 60_000).Action);
        var d = Eval(c, Direction.Outbound, 10_000);
        Assert.Equal(PacketActionKind.Drop, d.Action);
        Assert.Equal(DropReason.QueueOverflow, d.DropReason);
    }

    [Fact]
    public void OnDequeued_FreesQueueCapacity()
    {
        var c = Create(new ControlPolicy { Upload = BandwidthLimit.FromBytesPerSecond(1_000) });
        Eval(c, Direction.Outbound, 65_000);
        Eval(c, Direction.Outbound, 60_000);
        c.OnDequeued(Direction.Outbound, 60_000);
        Assert.NotEqual(PacketActionKind.Drop, Eval(c, Direction.Outbound, 10_000).Action);
    }

    [Fact]
    public void Counts_DroppedAndDelayed_PerTarget()
    {
        var c = Create(new ControlPolicy { Block = true });
        Eval(c, Direction.Outbound, 100);
        Eval(c, Direction.Inbound, 100);
        Assert.Equal(2, c.DroppedPackets);

        c.SetPolicy(new ControlPolicy { Reset = true });
        Eval(c, Direction.Outbound, 100, isTcp: true);   // Reset も破棄として数える
        Eval(c, Direction.Outbound, 100, isTcp: false);  // UDP は通過なので数えない
        Assert.Equal(3, c.DroppedPackets);

        c.SetPolicy(new ControlPolicy { Delay = TimeSpan.FromMilliseconds(10) });
        Eval(c, Direction.Outbound, 100);
        Assert.Equal(1, c.DelayedPackets);
        Assert.Equal(3, c.DroppedPackets); // ポリシーを変えても累計は保つ
    }

    [Fact]
    public void SetPolicy_TakesEffectImmediately()
    {
        var c = Create(new ControlPolicy());
        Assert.Equal(PacketActionKind.Pass, Eval(c, Direction.Outbound, 100).Action);
        c.SetPolicy(new ControlPolicy { Block = true });
        Assert.Equal(PacketActionKind.Drop, Eval(c, Direction.Outbound, 100).Action);
    }

    [Fact]
    public void Combine_DropBeatsResetBeatsDelayBeatsPass()
    {
        var pass = PacketDisposition.Pass;
        var delay = PacketDisposition.Delay(100);
        var later = PacketDisposition.Delay(200);
        var reset = PacketDisposition.Reset;
        var drop = PacketDisposition.Drop(DropReason.Block);

        Assert.Equal(drop, Dispositions.Combine(pass, drop));
        Assert.Equal(drop, Dispositions.Combine(reset, drop)); // 無音の破棄は Reset より優先
        Assert.Equal(reset, Dispositions.Combine(pass, reset));
        Assert.Equal(reset, Dispositions.Combine(delay, reset)); // Reset は遅延より優先
        Assert.Equal(later, Dispositions.Combine(delay, later));
        Assert.Equal(PacketActionKind.Pass, Dispositions.Combine(pass, pass).Action);
    }

    [Fact]
    public void PolicyIsActive_OnlyWhenSomethingEnabled()
    {
        Assert.False(new ControlPolicy().IsActive);
        Assert.True(new ControlPolicy { Block = true }.IsActive);
        Assert.True(new ControlPolicy { Reset = true }.IsActive);
        Assert.True(new ControlPolicy { DropProbability = 0.1 }.IsActive);
        Assert.True(new ControlPolicy { Delay = TimeSpan.FromMilliseconds(1) }.IsActive);
        Assert.True(new ControlPolicy { Upload = BandwidthLimit.Stopped }.IsActive);
        Assert.True(new ControlPolicy { Download = BandwidthLimit.FromBytesPerSecond(1_000) }.IsActive);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Validate_RejectsBadDropProbability(double p)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControlPolicy { DropProbability = p }.Validate());
    }

    [Fact]
    public void Validate_RejectsDelayBeyondMax()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ControlPolicy { Delay = TimeSpan.FromSeconds(11) }.Validate());
    }
}
