using NutstoreMcp;

namespace NutstoreMcp.Tests;

public class RateLimiterTests
{
    private const long T0 = 1_000_000;

    [Fact]
    public void AllowsUpToLimitThenFailsFastInsteadOfBlocking()
    {
        var limiter = new SlidingWindowRateLimiter(3, 1000);

        Assert.Equal(3, limiter.Remaining(T0));
        limiter.Acquire(T0);
        limiter.Acquire(T0 + 10);
        limiter.Acquire(T0 + 20);
        Assert.Equal(0, limiter.Remaining(T0 + 30));

        var started = DateTime.UtcNow;
        Assert.Throws<RateLimitedException>(() => limiter.Acquire(T0 + 30));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromMilliseconds(200), "必须立即失败，不能等待");
    }

    [Fact]
    public void BudgetRestoresAfterWindowSlides()
    {
        var limiter = new SlidingWindowRateLimiter(2, 1000);
        limiter.Acquire(T0);
        limiter.Acquire(T0 + 100);
        Assert.Throws<RateLimitedException>(() => limiter.Acquire(T0 + 200));

        Assert.Equal(1, limiter.Remaining(T0 + 1001));
        limiter.Acquire(T0 + 1001);
        Assert.Equal(0, limiter.Remaining(T0 + 1002));
    }

    [Fact]
    public void RetryAfterPointsAtOldestHitExpiry()
    {
        var limiter = new SlidingWindowRateLimiter(1, 60_000);
        limiter.Acquire(T0);
        Assert.Equal(60_000, limiter.RetryAfterMs(T0));
        Assert.Equal(1_000, limiter.RetryAfterMs(T0 + 59_000));
        Assert.Equal(0, limiter.RetryAfterMs(T0 + 60_001));
    }

    [Fact]
    public void ErrorMessageIsActionable()
    {
        var limiter = new SlidingWindowRateLimiter(1, 30 * 60_000);
        limiter.Acquire(T0);

        var ex = Assert.Throws<RateLimitedException>(() => limiter.Acquire(T0 + 60_000));
        Assert.Equal("RATE_LIMITED", ex.Code);
        Assert.Contains("30 分钟", ex.Message, StringComparison.Ordinal);
        Assert.Contains("1740 秒", ex.Hint!, StringComparison.Ordinal);
        Assert.Contains("NUTSTORE_RATE_LIMIT", ex.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotReflectsCurrentState()
    {
        var limiter = new SlidingWindowRateLimiter(10, 1000);
        limiter.Acquire(T0);
        var snap = limiter.Snapshot(T0);
        Assert.Equal(10, snap.Limit);
        Assert.Equal(1, snap.Used);
        Assert.Equal(9, snap.Remaining);
    }

    [Fact]
    public void ConstructorValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiter(0, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiter(-1, 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SlidingWindowRateLimiter(1, 0));
    }

    [Fact]
    public void ConcurrentAcquireNeverExceedsLimit()
    {
        const int limit = 50;
        var limiter = new SlidingWindowRateLimiter(limit, 60_000);
        var granted = 0;
        var now = RateLimiterTests.T0;

        Parallel.For(0, 200, _ =>
        {
            try
            {
                limiter.Acquire(now);
                Interlocked.Increment(ref granted);
            }
            catch (RateLimitedException)
            {
                // 预期内的拒绝
            }
        });

        Assert.Equal(limit, granted);
    }
}
