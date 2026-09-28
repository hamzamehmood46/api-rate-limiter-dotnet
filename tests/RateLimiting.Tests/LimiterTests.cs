using Microsoft.Extensions.Time.Testing;
using RateLimiting;

namespace RateLimiting.Tests;

public class TokenBucketTests
{
    private readonly FakeTimeProvider _time = new();

    private TokenBucketLimiter Bucket(int capacity = 5, double perSecond = 1) => new(capacity, perSecond, _time);

    [Fact]
    public void Allows_a_burst_up_to_capacity_then_denies()
    {
        var limiter = Bucket();

        for (var i = 0; i < 5; i++) Assert.True(limiter.Check("a").Allowed);

        var denied = limiter.Check("a");
        Assert.False(denied.Allowed);
        Assert.Equal(0, denied.Remaining);
    }

    [Fact]
    public void Remaining_counts_down()
    {
        var limiter = Bucket();

        Assert.Equal(4, limiter.Check("a").Remaining);
        Assert.Equal(3, limiter.Check("a").Remaining);
    }

    [Fact]
    public void Denied_result_says_how_long_until_a_token_is_available()
    {
        var limiter = Bucket(capacity: 2, perSecond: 0.5); // one token every 2s
        limiter.Check("a"); limiter.Check("a");

        var denied = limiter.Check("a");

        Assert.Equal(TimeSpan.FromSeconds(2), denied.RetryAfter);
    }

    [Fact]
    public void Tokens_refill_over_time_but_never_beyond_capacity()
    {
        var limiter = Bucket(capacity: 3, perSecond: 1);
        for (var i = 0; i < 3; i++) limiter.Check("a");

        _time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(limiter.Check("a").Allowed);
        Assert.True(limiter.Check("a").Allowed);
        Assert.False(limiter.Check("a").Allowed);

        _time.Advance(TimeSpan.FromHours(1)); // refill is capped
        Assert.Equal(2, limiter.Check("a").Remaining);
    }

    [Fact]
    public void Requests_can_cost_more_than_one_token()
    {
        var limiter = Bucket(capacity: 10);

        Assert.True(limiter.Check("a", cost: 7).Allowed);
        Assert.False(limiter.Check("a", cost: 7).Allowed);
        Assert.True(limiter.Check("a", cost: 3).Allowed);
    }

    [Fact]
    public void A_cost_larger_than_capacity_is_a_programming_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Bucket(capacity: 5).Check("a", cost: 6));
    }

    [Fact]
    public void Keys_are_isolated()
    {
        var limiter = Bucket(capacity: 1);

        Assert.True(limiter.Check("alice").Allowed);
        Assert.False(limiter.Check("alice").Allowed);
        Assert.True(limiter.Check("bob").Allowed);
    }

    [Fact]
    public void Concurrent_callers_can_never_exceed_the_capacity()
    {
        var limiter = Bucket(capacity: 100, perSecond: 1);
        var allowed = 0;

        Parallel.For(0, 1000, _ =>
        {
            if (limiter.Check("shared").Allowed) Interlocked.Increment(ref allowed);
        });

        Assert.Equal(100, allowed); // no time passed, so exactly the burst capacity gets through
    }

    [Fact]
    public void Eviction_drops_only_idle_keys()
    {
        var limiter = Bucket(capacity: 2, perSecond: 1);
        limiter.Check("busy"); limiter.Check("busy");
        limiter.Check("idle");
        Assert.Equal(2, limiter.TrackedKeys);

        _time.Advance(TimeSpan.FromSeconds(1)); // "idle" is full again, "busy" is still missing a token
        limiter.Evict();

        Assert.Equal(1, limiter.TrackedKeys);
        // "busy" kept its state: only the one refilled token is available. If eviction had wrongly reset it
        // to a full bucket, the second request would also be allowed.
        Assert.True(limiter.Check("busy").Allowed);
        Assert.False(limiter.Check("busy").Allowed);
    }

    [Fact]
    public void An_evicted_key_behaves_exactly_like_a_new_one()
    {
        var limiter = Bucket(capacity: 3, perSecond: 1);
        limiter.Check("a");
        _time.Advance(TimeSpan.FromMinutes(1));
        limiter.Evict();

        Assert.Equal(0, limiter.TrackedKeys);
        Assert.Equal(2, limiter.Check("a").Remaining);
    }

    [Fact]
    public void Eviction_racing_with_checks_never_loses_accounting()
    {
        // Hammer Check() from many threads while another thread keeps evicting. If eviction could orphan a
        // state that a caller then mutates, more than `capacity` requests would be allowed.
        var limiter = Bucket(capacity: 50, perSecond: 0.000001);
        var allowed = 0;
        using var stop = new CancellationTokenSource();
        var evictor = Task.Run(() => { while (!stop.IsCancellationRequested) limiter.Evict(); });

        Parallel.For(0, 2000, _ =>
        {
            if (limiter.Check("k").Allowed) Interlocked.Increment(ref allowed);
        });

        stop.Cancel();
        evictor.Wait();
        Assert.Equal(50, allowed);
    }
}

public class SlidingWindowTests
{
    private readonly FakeTimeProvider _time = new();

    private SlidingWindowLimiter Window(int limit = 3, int seconds = 10) => new(limit, TimeSpan.FromSeconds(seconds), _time);

    [Fact]
    public void Allows_up_to_the_limit_within_the_window()
    {
        var limiter = Window();

        for (var i = 0; i < 3; i++) Assert.True(limiter.Check("a").Allowed);
        Assert.False(limiter.Check("a").Allowed);
    }

    [Fact]
    public void Retry_after_is_when_the_oldest_request_leaves_the_window()
    {
        var limiter = Window();
        limiter.Check("a");
        _time.Advance(TimeSpan.FromSeconds(4));
        limiter.Check("a"); limiter.Check("a");

        var denied = limiter.Check("a");

        Assert.False(denied.Allowed);
        Assert.Equal(TimeSpan.FromSeconds(6), denied.RetryAfter); // first hit (t=0) expires at t=10; now is t=4
    }

    [Fact]
    public void Capacity_returns_as_requests_age_out()
    {
        var limiter = Window();
        for (var i = 0; i < 3; i++) limiter.Check("a");

        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.True(limiter.Check("a").Allowed);
    }

    [Fact]
    public void There_is_no_double_burst_at_a_window_boundary()
    {
        // A FIXED window would reset at t=10 and let this client send 3 more instantly (6 within 1.5s).
        var limiter = Window(limit: 3, seconds: 10);
        _time.Advance(TimeSpan.FromSeconds(9));
        for (var i = 0; i < 3; i++) Assert.True(limiter.Check("a").Allowed);

        _time.Advance(TimeSpan.FromSeconds(1.5)); // "new window" for a fixed-window limiter

        Assert.False(limiter.Check("a").Allowed); // sliding window still sees 3 hits in the last 10s
    }

    [Fact]
    public void Multi_cost_requests_reserve_multiple_slots()
    {
        var limiter = Window(limit: 5);

        Assert.True(limiter.Check("a", cost: 4).Allowed);
        Assert.False(limiter.Check("a", cost: 2).Allowed);
        Assert.True(limiter.Check("a", cost: 1).Allowed);
    }

    [Fact]
    public void Keys_are_isolated_and_idle_keys_can_be_evicted()
    {
        var limiter = Window(limit: 1);
        Assert.True(limiter.Check("alice").Allowed);
        Assert.True(limiter.Check("bob").Allowed);
        Assert.Equal(2, limiter.TrackedKeys);

        _time.Advance(TimeSpan.FromSeconds(11));
        limiter.Evict();

        Assert.Equal(0, limiter.TrackedKeys);
    }

    [Fact]
    public void Concurrent_callers_can_never_exceed_the_limit()
    {
        var limiter = Window(limit: 25);
        var allowed = 0;

        Parallel.For(0, 1000, _ =>
        {
            if (limiter.Check("shared").Allowed) Interlocked.Increment(ref allowed);
        });

        Assert.Equal(25, allowed);
    }
}
