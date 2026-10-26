using Microsoft.Extensions.Logging.Abstractions;
using NetworkTrafficController.Core.Control;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Control;

public class TimedBlockControllerTests
{
    private sealed class BlockFlag
    {
        public bool Blocked { get; private set; }
        public int Changes { get; private set; }

        public void Set(bool value)
        {
            Blocked = value;
            Changes++;
        }
    }

    private static (TimedBlockController Timed, BlockFlag Flag, FakeTimeProvider Time) Create()
    {
        var time = new FakeTimeProvider();
        var flag = new BlockFlag();
        return (new TimedBlockController(flag.Set, time, NullLogger.Instance), flag, time);
    }

    [Fact]
    public void Start_BlocksThenAutoRestores()
    {
        var (timed, flag, time) = Create();
        Assert.False(flag.Blocked);

        timed.Start(TimeSpan.FromSeconds(5));
        Assert.True(flag.Blocked);
        Assert.True(timed.IsActive);

        time.Advance(TimeSpan.FromSeconds(5));
        Assert.False(flag.Blocked);
        Assert.False(timed.IsActive);
    }

    [Fact]
    public void Cancel_RestoresImmediately_AndTimerDoesNotFireLater()
    {
        var (timed, flag, time) = Create();
        timed.Start(TimeSpan.FromSeconds(10));
        Assert.True(flag.Blocked);

        timed.Cancel();
        Assert.False(flag.Blocked);
        Assert.False(timed.IsActive);
        int changes = flag.Changes;

        time.Advance(TimeSpan.FromSeconds(20)); // 破棄済みタイマは発火しない
        Assert.False(flag.Blocked);
        Assert.Equal(changes, flag.Changes);
    }

    [Fact]
    public void Start_WhileActive_ReschedulesFromLatestStart()
    {
        var (timed, flag, time) = Create();
        timed.Start(TimeSpan.FromSeconds(5));
        time.Advance(TimeSpan.FromSeconds(3));
        timed.Start(TimeSpan.FromSeconds(5)); // 引き直し

        time.Advance(TimeSpan.FromSeconds(3)); // 最初のタイマの期限だが引き直し済み
        Assert.True(flag.Blocked);

        time.Advance(TimeSpan.FromSeconds(2)); // 2 回目の期限
        Assert.False(flag.Blocked);
    }

    [Fact]
    public void Dispose_RestoresBlock()
    {
        var (timed, flag, _) = Create();
        timed.Start(TimeSpan.FromSeconds(10));
        timed.Dispose();
        Assert.False(flag.Blocked);
    }
}
