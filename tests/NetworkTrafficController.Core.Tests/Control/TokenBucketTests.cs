using NetworkTrafficController.Core.Control;
using Xunit;

namespace NetworkTrafficController.Core.Tests.Control;

public class TokenBucketTests
{
    [Fact]
    public void Reserve_WithinCapacity_PassesImmediately()
    {
        var time = new FakeTimeProvider();
        var bucket = new TokenBucket(ratePerSecond: 1000, capacity: 1000, time);

        Assert.Equal(TimeSpan.Zero, bucket.Reserve(600));
        Assert.Equal(TimeSpan.Zero, bucket.Reserve(400));
        Assert.Equal(0, bucket.Tokens, 3);
    }

    [Fact]
    public void Reserve_BeyondTokens_ReturnsWaitForDeficit()
    {
        var time = new FakeTimeProvider();
        var bucket = new TokenBucket(ratePerSecond: 1000, capacity: 1000, time);
        bucket.Reserve(1000); // empty the bucket

        // 500 bytes more at 1000 B/s → 0.5 s wait
        TimeSpan wait = bucket.Reserve(500);
        Assert.Equal(0.5, wait.TotalSeconds, 3);
        Assert.Equal(-500, bucket.Tokens, 3);
    }

    [Fact]
    public void Refill_OverTime_RestoresTokensUpToCapacity()
    {
        var time = new FakeTimeProvider();
        var bucket = new TokenBucket(ratePerSecond: 1000, capacity: 1000, time);
        bucket.Reserve(1000);

        time.Advance(TimeSpan.FromSeconds(0.3));
        Assert.Equal(TimeSpan.Zero, bucket.Reserve(300)); // 300 tokens refilled exactly

        time.Advance(TimeSpan.FromSeconds(10));
        bucket.Reserve(0);
        Assert.Equal(1000, bucket.Tokens, 3); // capped at capacity
    }

    [Fact]
    public void SustainedRate_MatchesConfiguredRate()
    {
        var time = new FakeTimeProvider();
        var bucket = new TokenBucket(ratePerSecond: 10_000, capacity: 1_000, time);
        var releaseAt = TimeSpan.Zero;

        // 送信側が 100 KB を一気に投入した場合、最後のパケットの解放は約 (100000 - 1000) / 10000 = 9.9 s 後
        for (int i = 0; i < 100; i++)
        {
            var wait = bucket.Reserve(1000);
            if (wait > releaseAt)
            {
                releaseAt = wait;
            }
        }

        Assert.Equal(9.9, releaseAt.TotalSeconds, 2);
    }

    [Fact]
    public void Reserve_HugeDeficit_WaitIsCapped()
    {
        var bucket = new TokenBucket(ratePerSecond: 1000, capacity: 1000, new FakeTimeProvider());
        TimeSpan wait = bucket.Reserve(int.MaxValue);
        Assert.Equal(TokenBucket.MaxWait, wait);
    }

    [Fact]
    public void Reserve_WithZeroRate_Throws()
    {
        var bucket = new TokenBucket(ratePerSecond: 0, capacity: 1000, new FakeTimeProvider());
        Assert.Throws<InvalidOperationException>(() => bucket.Reserve(1));
    }

    [Fact]
    public void Reconfigure_ClampsTokensToNewCapacity()
    {
        var time = new FakeTimeProvider();
        var bucket = new TokenBucket(ratePerSecond: 1000, capacity: 5000, time);
        bucket.Reconfigure(1000, 100);
        Assert.Equal(100, bucket.Tokens, 3);
    }
}
