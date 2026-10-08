namespace NutstoreMcp;

/// <summary>
/// 滑动窗口限流器。
///
/// 存在的理由：坚果云 WebDAV 的官方限制是 **600 次 / 30 分钟**（免费版，
/// 付费 1500）。超限会影响账号的正常同步 —— 所以宁可本地先挡住，
/// 也不要等服务端 429。
///
/// 设计取舍：**快速失败，不排队等待**。一个 MCP 工具调用如果被告知
/// 「配额用尽，请 12 分钟后再试」，模型可以换个策略；如果挂在那里等
/// 12 分钟，调用方只会超时，而且看不出原因。
/// </summary>
public sealed class SlidingWindowRateLimiter
{
    private readonly int _limit;
    private readonly long _windowMs;
    private readonly Queue<long> _hits = new();
    private readonly Lock _gate = new();

    public SlidingWindowRateLimiter(int limit, long windowMs)
    {
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "limit must be positive");
        }

        if (windowMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowMs), "windowMs must be positive");
        }

        _limit = limit;
        _windowMs = windowMs;
    }

    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private void Prune(long now)
    {
        var cutoff = now - _windowMs;
        while (_hits.Count > 0 && _hits.Peek() <= cutoff)
        {
            _hits.Dequeue();
        }
    }

    public int Used(long now)
    {
        lock (_gate)
        {
            Prune(now);
            return _hits.Count;
        }
    }

    public int Remaining(long now) => Math.Max(0, _limit - Used(now));

    /// <summary>还要等多久才能再发一次（毫秒）。配额未用尽时为 0。</summary>
    public long RetryAfterMs(long now)
    {
        lock (_gate)
        {
            Prune(now);
            if (_hits.Count < _limit)
            {
                return 0;
            }

            // 队列里第 (Count - limit + 1) 个元素是「最早那次仍占用配额的请求」
            var skip = _hits.Count - _limit;
            var idx = 0;
            foreach (var t in _hits)
            {
                if (idx++ == skip)
                {
                    return Math.Max(0, t + _windowMs - now);
                }
            }

            return 0;
        }
    }

    public RateLimitSnapshot Snapshot(long now)
    {
        var used = Used(now);
        return new RateLimitSnapshot
        {
            Limit = _limit,
            WindowMs = _windowMs,
            Used = used,
            Remaining = Math.Max(0, _limit - used),
            RetryAfterMs = RetryAfterMs(now),
        };
    }

    /// <summary>
    /// 申请一次配额。配额用尽时抛 <see cref="RateLimitedException"/>，**不阻塞**。
    /// </summary>
    public int Acquire(long now)
    {
        lock (_gate)
        {
            Prune(now);
            if (_hits.Count >= _limit)
            {
                var waitMs = RetryAfterMs(now);
                var waitSec = (long)Math.Ceiling(waitMs / 1000.0);
                throw new RateLimitedException(
                    $"WebDAV 请求配额已用尽（{_limit} 次 / {Math.Round(_windowMs / 60000.0)} 分钟）",
                    $"请等待约 {waitSec} 秒后重试。若需更高配额，可调整 NUTSTORE_RATE_LIMIT；"
                    + "付费账号官方上限为 1500 次 / 30 分钟。");
            }

            _hits.Enqueue(now);
            return Math.Max(0, _limit - _hits.Count);
        }
    }
}

public sealed class RateLimitSnapshot
{
    public int Limit { get; init; }
    public long WindowMs { get; init; }
    public int Used { get; init; }
    public int Remaining { get; init; }
    public long RetryAfterMs { get; init; }
}
