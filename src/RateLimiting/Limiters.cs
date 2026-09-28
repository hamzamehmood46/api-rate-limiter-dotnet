using System.Collections.Concurrent;

namespace RateLimiting;

/// <param name="Allowed">Whether the request may proceed.</param>
/// <param name="Limit">The maximum burst/requests for the policy.</param>
/// <param name="Remaining">How much capacity is left after this request.</param>
/// <param name="RetryAfter">When denied: how long until the request would be allowed. Zero when allowed.</param>
/// <param name="ResetAfter">How long until full capacity is restored.</param>
public readonly record struct RateLimitResult(bool Allowed, int Limit, int Remaining, TimeSpan RetryAfter, TimeSpan ResetAfter);

public interface IRateLimiter
{
    /// <summary>Consumes <paramref name="cost"/> units for <paramref name="key"/> if available.</summary>
    RateLimitResult Check(string key, int cost = 1);

    /// <summary>Number of keys currently holding state (for monitoring memory use).</summary>
    int TrackedKeys { get; }

    /// <summary>Drops state for keys that have been idle long enough to be indistinguishable from new ones.</summary>
    void Evict();
}

/// <summary>
/// Shared per-key state handling. Each key has its own lock, so different clients never contend with each other,
/// and eviction is safe: an evicted state is flagged so a racing caller retries on a fresh one instead of
/// updating an orphan (which would silently lose its accounting).
/// </summary>
public abstract class KeyedLimiter<TState> : IRateLimiter where TState : KeyedLimiter<TState>.StateBase, new()
{
    public abstract class StateBase
    {
        public readonly object Gate = new();
        public bool Evicted;
    }

    private readonly ConcurrentDictionary<string, TState> _states = new();

    protected KeyedLimiter(TimeProvider? time) => Time = time ?? TimeProvider.System;

    protected TimeProvider Time { get; }

    public int TrackedKeys => _states.Count;

    public RateLimitResult Check(string key, int cost = 1)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (cost < 1) throw new ArgumentOutOfRangeException(nameof(cost), "Cost must be at least 1.");
        ValidateCost(cost);

        while (true)
        {
            var state = _states.GetOrAdd(key, _ => new TState());
            lock (state.Gate)
            {
                if (state.Evicted) continue; // lost a race with Evict(); take the replacement state
                return Consume(state, cost, Time.GetUtcNow());
            }
        }
    }

    public void Evict()
    {
        var now = Time.GetUtcNow();
        foreach (var (key, state) in _states)
        {
            lock (state.Gate)
            {
                if (!IsIdle(state, now)) continue;
                state.Evicted = true;
                _states.TryRemove(new KeyValuePair<string, TState>(key, state));
            }
        }
    }

    protected abstract void ValidateCost(int cost);
    protected abstract RateLimitResult Consume(TState state, int cost, DateTimeOffset now);
    protected abstract bool IsIdle(TState state, DateTimeOffset now);
}

/// <summary>
/// Token bucket: holds up to <c>capacity</c> tokens and refills at a steady rate. Allows short bursts up to the
/// capacity while enforcing the long-run average, which suits most public APIs.
/// </summary>
public sealed class TokenBucketLimiter : KeyedLimiter<TokenBucketLimiter.Bucket>
{
    public sealed class Bucket : StateBase
    {
        public double Tokens = double.NaN; // NaN = untouched, treated as full
        public DateTimeOffset LastRefill;
    }

    private readonly int _capacity;
    private readonly double _refillPerSecond;

    public TokenBucketLimiter(int capacity, double refillPerSecond, TimeProvider? time = null) : base(time)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (refillPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(refillPerSecond));
        _capacity = capacity;
        _refillPerSecond = refillPerSecond;
    }

    protected override void ValidateCost(int cost)
    {
        if (cost > _capacity) throw new ArgumentOutOfRangeException(nameof(cost), "Cost exceeds the bucket capacity and could never be allowed.");
    }

    protected override RateLimitResult Consume(Bucket bucket, int cost, DateTimeOffset now)
    {
        Refill(bucket, now);

        if (bucket.Tokens >= cost)
        {
            bucket.Tokens -= cost;
            return new RateLimitResult(true, _capacity, (int)Math.Floor(bucket.Tokens), TimeSpan.Zero, UntilFull(bucket));
        }

        var retry = TimeSpan.FromSeconds((cost - bucket.Tokens) / _refillPerSecond);
        return new RateLimitResult(false, _capacity, (int)Math.Floor(bucket.Tokens), retry, UntilFull(bucket));
    }

    protected override bool IsIdle(Bucket bucket, DateTimeOffset now)
    {
        Refill(bucket, now);
        return bucket.Tokens >= _capacity;
    }

    private void Refill(Bucket bucket, DateTimeOffset now)
    {
        if (double.IsNaN(bucket.Tokens))
        {
            bucket.Tokens = _capacity;
        }
        else
        {
            var elapsed = (now - bucket.LastRefill).TotalSeconds;
            if (elapsed > 0) bucket.Tokens = Math.Min(_capacity, bucket.Tokens + elapsed * _refillPerSecond);
        }

        bucket.LastRefill = now;
    }

    private TimeSpan UntilFull(Bucket bucket) => TimeSpan.FromSeconds((_capacity - bucket.Tokens) / _refillPerSecond);
}

/// <summary>
/// Sliding-window log: at most <c>limit</c> requests in ANY window of the given length. Unlike a fixed window
/// (which lets a client send <c>limit</c> requests at 0:59 and <c>limit</c> more at 1:00), this cannot be
/// gamed at window boundaries. It costs memory proportional to the limit per key.
/// </summary>
public sealed class SlidingWindowLimiter : KeyedLimiter<SlidingWindowLimiter.Window>
{
    public sealed class Window : StateBase
    {
        public readonly Queue<DateTimeOffset> Hits = new();
    }

    private readonly int _limit;
    private readonly TimeSpan _window;

    public SlidingWindowLimiter(int limit, TimeSpan window, TimeProvider? time = null) : base(time)
    {
        if (limit < 1) throw new ArgumentOutOfRangeException(nameof(limit));
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _limit = limit;
        _window = window;
    }

    protected override void ValidateCost(int cost)
    {
        if (cost > _limit) throw new ArgumentOutOfRangeException(nameof(cost), "Cost exceeds the limit and could never be allowed.");
    }

    protected override RateLimitResult Consume(Window window, int cost, DateTimeOffset now)
    {
        Purge(window, now);

        if (window.Hits.Count + cost <= _limit)
        {
            for (var i = 0; i < cost; i++) window.Hits.Enqueue(now);
            return new RateLimitResult(true, _limit, _limit - window.Hits.Count, TimeSpan.Zero, window.Hits.Peek() + _window - now);
        }

        // Denied: we need (count + cost - limit) of the oldest hits to expire before this would fit.
        var mustExpire = window.Hits.Count + cost - _limit;
        var retry = window.Hits.ElementAt(mustExpire - 1) + _window - now;
        var reset = window.Hits.Count == 0 ? TimeSpan.Zero : window.Hits.Peek() + _window - now;
        return new RateLimitResult(false, _limit, _limit - window.Hits.Count, retry, reset);
    }

    protected override bool IsIdle(Window window, DateTimeOffset now)
    {
        Purge(window, now);
        return window.Hits.Count == 0;
    }

    private void Purge(Window window, DateTimeOffset now)
    {
        while (window.Hits.Count > 0 && window.Hits.Peek() + _window <= now) window.Hits.Dequeue();
    }
}
