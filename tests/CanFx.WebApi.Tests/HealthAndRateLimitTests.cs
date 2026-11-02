using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CanFx.WebApi.DependencyInjection;
using CanFx.WebApi.HealthChecks;
using CanFx.WebApi.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CanFx.WebApi.Tests;

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
    public async Task Custom_policy_from_configuration_limits_its_endpoint()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:Policies:reports:PermitLimit"] = "1",
            ["RateLimiting:Policies:reports:Window"] = "00:01:00",
            ["RateLimiting:Policies:reports:PartitionBy"] = "Tenant",
        });
        builder.Services.AddCanWebApi();
        builder.Services.AddCanRateLimiting(o => builder.Configuration.GetSection("RateLimiting").Bind(o));

        await using WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.UseCanRateLimiting();
        app.MapGet("/reports", () => TypedResults.Ok()).RequireRateLimiting("reports");
        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/reports")).StatusCode);
    }

    [Fact]
    public async Task Unknown_policy_name_stops_the_application_at_startup()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddCanRateLimiting();

        await using WebApplication app = builder.Build();
        app.UseCanRateLimiting();
        app.MapGet("/export", () => TypedResults.Ok()).RequireRateLimiting("exprot");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        Assert.Contains("'exprot'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Policies_added_by_the_application_in_code_are_accepted()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddCanRateLimiting();
        builder.Services.AddRateLimiter(o => o.AddPolicy("own", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("own")));

        await using WebApplication app = builder.Build();
        app.UseCanRateLimiting();
        app.MapGet("/own", () => TypedResults.Ok()).RequireRateLimiting("own");

        await app.StartAsync();   // fırlatmaz
        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().GetAsync("/own")).StatusCode);
    }

    [Fact]
    public void Invalid_custom_policy_is_rejected_when_registering()
    {
        var services = new ServiceCollection();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            services.AddCanRateLimiting(o => o.Policies["reports"] = new RateLimitRule { PermitLimit = 0, Window = TimeSpan.FromMinutes(1) }));
        Assert.Contains("Policies.reports.PermitLimit", error.Message, StringComparison.Ordinal);
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
