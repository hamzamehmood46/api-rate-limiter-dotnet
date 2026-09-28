using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace RateLimiting;

public class RateLimitOptions
{
    /// <summary>Identifies the client. Default: the X-Api-Key header, else the remote IP, else "anonymous".</summary>
    public Func<HttpContext, string> KeySelector { get; set; } = DefaultKey;

    /// <summary>Requests for which this returns true (health checks, for example) are never limited.</summary>
    public Func<HttpContext, bool> Exempt { get; set; } = _ => false;

    private static string DefaultKey(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Api-Key", out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
            return "key:" + apiKey;

        var ip = context.Connection.RemoteIpAddress;
        return ip is null ? "anonymous" : "ip:" + ip;
    }
}

/// <summary>
/// Enforces a limiter on every request and reports the state to the client using the standard headers, so
/// well-behaved clients can back off on their own: RateLimit-Limit, RateLimit-Remaining, RateLimit-Reset
/// (seconds), and Retry-After on 429 responses.
/// </summary>
public class RateLimitMiddleware(RequestDelegate next, IRateLimiter limiter, RateLimitOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (options.Exempt(context))
        {
            await next(context);
            return;
        }

        var result = limiter.Check(options.KeySelector(context));

        var headers = context.Response.Headers;
        headers["RateLimit-Limit"] = result.Limit.ToString();
        headers["RateLimit-Remaining"] = result.Remaining.ToString();
        headers["RateLimit-Reset"] = CeilSeconds(result.ResetAfter).ToString();

        if (result.Allowed)
        {
            await next(context);
            return;
        }

        var retryAfter = Math.Max(1, CeilSeconds(result.RetryAfter));
        headers["Retry-After"] = retryAfter.ToString();
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = StatusCodes.Status429TooManyRequests,
            Title = "Too many requests",
            Detail = $"Rate limit exceeded. Retry after {retryAfter} second(s)."
        }, options: null, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }

    private static int CeilSeconds(TimeSpan span) => (int)Math.Ceiling(Math.Max(0, span.TotalSeconds));
}

public static class RateLimitingExtensions
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services, IRateLimiter limiter, Action<RateLimitOptions>? configure = null)
    {
        var options = new RateLimitOptions();
        configure?.Invoke(options);
        return services.AddSingleton(limiter).AddSingleton(options);
    }

    public static IApplicationBuilder UseApiRateLimiting(this IApplicationBuilder app) => app.UseMiddleware<RateLimitMiddleware>();
}
