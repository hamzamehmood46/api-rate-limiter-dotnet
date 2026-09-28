using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using RateLimiting;

namespace RateLimiting.Tests;

public class MiddlewareTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly List<IHost> _hosts = [];

    private HttpClient Client(int capacity = 3, Action<RateLimitOptions>? configure = null)
    {
        var limiter = new TokenBucketLimiter(capacity, refillPerSecond: 1, _time);
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s => s.AddApiRateLimiting(limiter, configure))
                .Configure(app =>
                {
                    app.UseApiRateLimiting();
                    app.Run(ctx => ctx.Response.WriteAsync("ok"));
                }))
            .Build();
        host.Start();
        _hosts.Add(host);
        return host.GetTestClient();
    }

    private static HttpRequestMessage Get(string path = "/", string? apiKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (apiKey is not null) request.Headers.Add("X-Api-Key", apiKey);
        return request;
    }

    private static int Header(HttpResponseMessage response, string name) =>
        int.Parse(response.Headers.GetValues(name).Single());

    [Fact]
    public async Task Allowed_requests_report_the_remaining_budget_in_headers()
    {
        var client = Client(capacity: 3);

        var first = await client.SendAsync(Get(apiKey: "k"));
        var second = await client.SendAsync(Get(apiKey: "k"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(3, Header(first, "RateLimit-Limit"));
        Assert.Equal(2, Header(first, "RateLimit-Remaining"));
        Assert.Equal(1, Header(second, "RateLimit-Remaining"));
    }

    [Fact]
    public async Task Exceeding_the_limit_returns_429_with_retry_after_and_problem_details()
    {
        var client = Client(capacity: 2);
        await client.SendAsync(Get(apiKey: "k"));
        await client.SendAsync(Get(apiKey: "k"));

        var denied = await client.SendAsync(Get(apiKey: "k"));

        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        Assert.Equal(1, Header(denied, "Retry-After")); // one token per second
        Assert.Equal(0, Header(denied, "RateLimit-Remaining"));
        Assert.Equal("application/problem+json", denied.Content.Headers.ContentType!.MediaType);

        using var body = JsonDocument.Parse(await denied.Content.ReadAsStringAsync());
        Assert.Equal(429, body.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Client_recovers_after_waiting_the_advertised_time()
    {
        var client = Client(capacity: 1);
        await client.SendAsync(Get(apiKey: "k"));
        var denied = await client.SendAsync(Get(apiKey: "k"));

        _time.Advance(TimeSpan.FromSeconds(Header(denied, "Retry-After")));

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get(apiKey: "k"))).StatusCode);
    }

    [Fact]
    public async Task Each_api_key_has_its_own_budget()
    {
        var client = Client(capacity: 1);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get(apiKey: "alice"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get(apiKey: "alice"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get(apiKey: "bob"))).StatusCode);
    }

    [Fact]
    public async Task Requests_without_a_key_share_a_fallback_budget()
    {
        var client = Client(capacity: 1);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get())).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get())).StatusCode);
    }

    [Fact]
    public async Task Exempt_paths_are_never_limited()
    {
        var client = Client(capacity: 1, o => o.Exempt = ctx => ctx.Request.Path == "/health");

        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/health", "k"))).StatusCode);
    }

    [Fact]
    public async Task Custom_key_selector_is_honoured()
    {
        var client = Client(capacity: 1, o => o.KeySelector = ctx => ctx.Request.Query["tenant"].ToString());

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/?tenant=a"))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Get("/?tenant=a"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/?tenant=b"))).StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            await host.StopAsync();
            host.Dispose();
        }
    }
}
