namespace RateLimiting.Playground;

public enum Algo { TokenBucket, SlidingWindow, FixedWindow }

public sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The naive "N per minute, reset on the minute" limiter, here only for comparison. It is not part of the library
/// because it can be gamed at window boundaries, which the comparison below demonstrates.
/// </summary>
public sealed class FixedWindowLimiter : IRateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<string, (long Window, int Count)> _state = [];

    public FixedWindowLimiter(int limit, TimeSpan window, TimeProvider time)
    {
        _limit = limit;
        _window = window;
        _time = time;
    }

    public int TrackedKeys
    {
        get { lock (_gate) { return _state.Count; } }
    }

    public void Evict()
    {
        lock (_gate)
        {
            var current = (long)(_time.GetUtcNow().ToUnixTimeSeconds() / _window.TotalSeconds);
            foreach (var key in _state.Where(s => s.Value.Window < current).Select(s => s.Key).ToList())
            {
                _state.Remove(key);
            }
        }
    }

    public RateLimitResult Check(string key, int cost = 1)
    {
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var window = (long)(now / _window.TotalSeconds);
        var untilReset = TimeSpan.FromSeconds((window + 1) * _window.TotalSeconds - now);
        lock (_gate)
        {
            var (w, count) = _state.TryGetValue(key, out var s) ? s : (window, 0);
            if (w != window)
            {
                count = 0;
            }

            if (count + cost > _limit)
            {
                _state[key] = (window, count);
                return new RateLimitResult(false, _limit, Math.Max(0, _limit - count), untilReset, untilReset);
            }

            _state[key] = (window, count + cost);
            return new RateLimitResult(true, _limit, _limit - count - cost, TimeSpan.Zero, untilReset);
        }
    }
}

public sealed record Row(string Time, string Client, bool Allowed, int Limit, int Remaining, double RetryAfter);

public sealed record Comparison(string Name, int Allowed, string Note);

/// <summary>The real limiters from the library, on a clock the visitor moves by hand.</summary>
public sealed class Lab
{
    public Algo Algorithm { get; set; } = Algo.TokenBucket;
    public int Capacity { get; set; } = 5;
    public double RefillPerSecond { get; set; } = 1;
    public int WindowSeconds { get; set; } = 10;

    public ManualTime Time { get; private set; } = new();
    public IRateLimiter Limiter { get; private set; } = null!;
    public List<Row> Rows { get; } = [];
    public Dictionary<string, (int Allowed, int Denied)> Totals { get; } = new() { ["Ava"] = (0, 0), ["Ben"] = (0, 0) };
    public int Elapsed { get; private set; }
    public RateLimitResult? Last { get; private set; }
    public string? LastClient { get; private set; }

    public Lab() => Build();

    public void Build()
    {
        Time = new ManualTime();
        Elapsed = 0;
        Limiter = Make(Algorithm, Time, Capacity, RefillPerSecond, WindowSeconds);
        Rows.Clear();
        foreach (var key in Totals.Keys.ToList())
        {
            Totals[key] = (0, 0);
        }

        Last = null;
    }

    public static IRateLimiter Make(Algo algorithm, TimeProvider time, int capacity, double refill, int windowSeconds) => algorithm switch
    {
        Algo.TokenBucket => new TokenBucketLimiter(capacity, refill, time),
        Algo.SlidingWindow => new SlidingWindowLimiter(capacity, TimeSpan.FromSeconds(windowSeconds), time),
        _ => new FixedWindowLimiter(capacity, TimeSpan.FromSeconds(windowSeconds), time),
    };

    public string Clock => $"{Elapsed / 60:00}:{Elapsed % 60:00}";

    public void Send(string client, int count = 1)
    {
        for (var i = 0; i < count; i++)
        {
            var result = Limiter.Check(client);
            Last = result;
            LastClient = client;
            var (a, d) = Totals[client];
            Totals[client] = result.Allowed ? (a + 1, d) : (a, d + 1);
            Rows.Insert(0, new Row(Clock, client, result.Allowed, result.Limit, result.Remaining, Math.Ceiling(result.RetryAfter.TotalSeconds)));
        }

        if (Rows.Count > 200)
        {
            Rows.RemoveRange(200, Rows.Count - 200);
        }
    }

    public void Advance(int seconds)
    {
        Time.Advance(TimeSpan.FromSeconds(seconds));
        Elapsed += seconds;
    }

    /// <summary>A client sends a full quota just before a window boundary and another just after.</summary>
    public IReadOnlyList<Comparison> BoundaryAttack()
    {
        var results = new List<Comparison>();
        foreach (var (algo, name) in new[] { (Algo.FixedWindow, "Fixed window"), (Algo.SlidingWindow, "Sliding window"), (Algo.TokenBucket, "Token bucket") })
        {
            var time = new ManualTime();
            var limiter = Make(algo, time, Capacity, Capacity / (double)WindowSeconds, WindowSeconds);
            var allowed = 0;
            time.Advance(TimeSpan.FromSeconds(WindowSeconds - 1));
            for (var i = 0; i < Capacity; i++)
            {
                allowed += limiter.Check("attacker").Allowed ? 1 : 0;
            }

            time.Advance(TimeSpan.FromSeconds(2));
            for (var i = 0; i < Capacity; i++)
            {
                allowed += limiter.Check("attacker").Allowed ? 1 : 0;
            }

            results.Add(new Comparison(name, allowed, algo switch
            {
                Algo.FixedWindow => $"{allowed} requests in 2 seconds against a limit of {Capacity} per {WindowSeconds} s: the quota was used twice across the boundary.",
                Algo.SlidingWindow => $"Never more than {Capacity} in any {WindowSeconds}-second span, wherever the boundary falls.",
                _ => "A burst is allowed by design, then only what has refilled since.",
            }));
        }

        return results;
    }

    public void Evict() => Limiter.Evict();
}
