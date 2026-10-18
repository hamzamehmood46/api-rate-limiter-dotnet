# API Rate Limiter

[![Test and deploy](https://github.com/hamzamehmood46/api-rate-limiter-dotnet/actions/workflows/pages.yml/badge.svg)](https://github.com/hamzamehmood46/api-rate-limiter-dotnet/actions/workflows/pages.yml)

**Live playground: [hamzamehmood46.github.io/api-rate-limiter-dotnet](https://hamzamehmood46.github.io/api-rate-limiter-dotnet/)**. Send requests as two clients, move a simulated clock, and see the real limiters decide. The page also runs a boundary attack that shows a fixed window letting twice the quota through (10 requests in 2 seconds against a limit of 5), where the sliding window allows 5 and the token bucket 6.
 limiters are compiled straight into the browser app from [Limiters.cs](src/RateLimiting/Limiters.cs) (the middleware stays server-side).
Two rate-limiting algorithms and an **ASP.NET Core middleware** for **.NET 8**, built from scratch: **token bucket** and **sliding-window log**, per-client, thread-safe, and communicating limits to clients through the standard `RateLimit-*` and `Retry-After` headers.

(.NET ships a built-in `RateLimiter`; this project exists to show how the algorithms actually work and the concurrency problems underneath them.)

## Usage

```csharp
builder.Services.AddApiRateLimiting(
    new TokenBucketLimiter(capacity: 100, refillPerSecond: 10),
    o => o.Exempt = ctx => ctx.Request.Path == "/health");

app.UseApiRateLimiting();
```

Clients are identified by the `X-Api-Key` header, falling back to their IP address (override with `KeySelector`).

```
HTTP/1.1 200 OK                      HTTP/1.1 429 Too Many Requests
RateLimit-Limit: 100                 RateLimit-Limit: 100
RateLimit-Remaining: 42              RateLimit-Remaining: 0
RateLimit-Reset: 6                   Retry-After: 1
                                     Content-Type: application/problem+json
```

## The algorithms

| | Token bucket | Sliding-window log |
|---|---|---|
| Rule | Bucket holds up to *capacity* tokens, refilled at a steady rate. Each request spends one (or more). | At most *limit* requests in **any** window of the given length. |
| Good for | Allowing short bursts while enforcing a long-run average | Strict "N per minute" guarantees |
| Memory per client | Constant (two numbers) | Up to *limit* timestamps |
| Weak spot | Bursts up to capacity are allowed by design | Higher memory at large limits |

A **fixed** window (reset every minute on the minute) lets a client send a full quota at 0:59 and another at 1:00. The sliding window can't be gamed that way, and a test proves it.

## Engineering notes

- **Per-key locking.** Each client has its own lock, so one client's traffic never blocks another's.
- **Safe eviction.** Idle keys are dropped so memory doesn't grow forever. Dropping a key while another thread is mid-request could orphan its state and silently under-count. Evicted states are flagged under the lock and racing callers retry on a fresh state. A test hammers `Check()` from many threads while another thread evicts continuously and asserts the limit is never exceeded.
- **Testable time.** Everything uses `TimeProvider`, so refill and window expiry are tested exactly with `FakeTimeProvider`, with no sleeps and no flaky tests.
- **Honest retry hints.** `Retry-After` is computed from the actual state (time until the next token, or until enough old requests age out), so clients that obey it succeed on the first retry. A test does exactly this.
- **Cost-aware.** Expensive endpoints can consume several units per request; a request that could never fit is rejected as a programming error.

## Tests

```bash
dotnet test
```

25 tests, run repeatedly to check for flakiness: burst then deny, refill and cap, retry timing, multi-cost requests, key isolation, eviction correctness, the boundary-burst case, **1,000 concurrent callers never exceeding the limit**, **eviction racing with checks**, and the HTTP contract (headers, 429 problem details, recovery after `Retry-After`, per-key budgets, exempt paths, custom key selector).

## Production notes

- This limiter is **per process**. Behind several instances, use a shared store (a Redis Lua script is the usual approach) so the limit is global.
- If clients sit behind a proxy, key on the forwarded client address (configure `ForwardedHeaders`) rather than the proxy's IP.
- Call `Evict()` periodically (a hosted timer) to bound memory.

## Deploying

Every push to `main` runs the tests and, if they pass, publishes the playground to GitHub Pages (see `.github/workflows/pages.yml`).

## License

MIT
