using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Can.Core.WebApi.DependencyInjection;
using Can.Core.WebApi.HealthChecks;
using Can.Core.WebApi.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Can.Core.WebApi.Tests;

public class HealthAndRateLimitTests
{
    [Fact]
    public async Task Live_ignores_checks_and_ready_reports_them()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddHealthChecks()
            .AddCheck("db", () => HealthCheckResult.Healthy(), ["ready"])
            .AddCheck("redis", () => HealthCheckResult.Unhealthy("bağlantı yok"), ["ready"]);

        await using WebApplication app = builder.Build();
        app.MapCanHealthChecks();
        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        HttpResponseMessage live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        JsonElement liveBody = await live.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", liveBody.GetProperty("status").GetString());
        Assert.Empty(liveBody.GetProperty("checks").EnumerateObject());

        HttpResponseMessage ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        JsonElement checks = (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("checks");
        Assert.Equal("Healthy", checks.GetProperty("db").GetProperty("status").GetString());
        Assert.Equal("Unhealthy", checks.GetProperty("redis").GetProperty("status").GetString());
        Assert.Equal("bağlantı yok", checks.GetProperty("redis").GetProperty("description").GetString());
    }

    [Fact]
    public async Task Auth_policy_rejects_with_429_problem_and_retry_after()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddCanRateLimiting(o => o.Auth = new RateLimitRule { PermitLimit = 2, Window = TimeSpan.FromMinutes(1) });

        await using WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.UseCanRateLimiting();
        app.MapPost("/login", () => TypedResults.Ok()).RequireRateLimiting(CanRateLimitPolicies.Auth);
        app.MapGet("/free", () => TypedResults.Ok());
        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/login", null)).StatusCode);

        HttpResponseMessage rejected = await client.PostAsync("/login", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.True(rejected.Headers.RetryAfter is not null);
        JsonElement body = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("rate_limited", body.GetProperty("code").GetString());

        // Başka uç noktalar yalnızca genel sınıra tabi.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/free")).StatusCode);
    }

    [Fact]
    public async Task Distributed_factory_replaces_in_memory_counters()
    {
        var factory = new CountingFactory();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddCanRateLimiting(o => o.Auth = new RateLimitRule { PermitLimit = 1, Window = TimeSpan.FromMinutes(1) });
        builder.Services.AddSingleton<IDistributedRateLimiterFactory>(factory);

        await using WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.UseCanRateLimiting();
        app.MapPost("/login", () => TypedResults.Ok()).RequireRateLimiting(CanRateLimitPolicies.Auth);
        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/login", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/login", null)).StatusCode);

        // Hem genel hem giriş sınırı dağıtık sınırlayıcıdan geldi (sayaçlar sunucunun belleğinde değil).
        Assert.Contains(factory.Keys, k => k.StartsWith("global:", StringComparison.Ordinal));
        Assert.Contains(factory.Keys, k => k.StartsWith("auth:", StringComparison.Ordinal));
    }

    /// <summary>Dağıtık sınırlayıcı yerine: bölüm başına sabit pencere (yalnızca hangi anahtarların istendiğini görmek için).</summary>
    private sealed class CountingFactory : IDistributedRateLimiterFactory
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Keys { get; } = [];

        public System.Threading.RateLimiting.RateLimiter Create(string partitionKey, RateLimitRule rule)
        {
            Keys.Add(partitionKey);
            return new System.Threading.RateLimiting.FixedWindowRateLimiter(new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = rule.PermitLimit,
                Window = rule.Window,
                QueueLimit = 0,
            });
        }
    }
}
