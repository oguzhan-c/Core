using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using CanFx.Application;
using CanFx.WebApi.DependencyInjection;
using CanFx.WebApi.RequestLogging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.WebApi.Tests;

public class RequestActivityLogTests
{
    private sealed class Recorder(TimeSpan delay = default) : IRequestActivitySink
    {
        public ConcurrentQueue<RequestActivity> Items { get; } = new();

        public async ValueTask WriteAsync(IReadOnlyList<RequestActivity> batch, CancellationToken cancellationToken = default)
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);
            foreach (RequestActivity item in batch)
                Items.Enqueue(item);
        }

        public async Task<RequestActivity[]> WaitForAsync(int count)
        {
            var watch = Stopwatch.StartNew();
            while (Items.Count < count && watch.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(20);
            return [.. Items];
        }
    }

    private static async Task<(WebApplication App, HttpClient Client)> CreateAsync(Recorder recorder, Action<RequestActivityOptions>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCanWebApi();
        builder.Services.AddCanRequestActivityLog(o =>
        {
            o.FlushInterval = TimeSpan.FromMilliseconds(50);
            configure?.Invoke(o);
        });
        builder.Services.AddSingleton<IRequestActivitySink>(recorder);

        WebApplication app = builder.Build();
        app.UseCanExceptionHandler();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-User", out var user))
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user.ToString())], "Test"));
            await next(context);
        });
        app.UseCanTenantResolution();
        app.UseCanRequestActivityLog();
        app.MapGet("/api/orders/{id:int}", (int id) => TypedResults.Ok(id));
        app.MapPost("/api/orders", () => TypedResults.Created("/api/orders/1", 1));
        app.MapGet("/api/fail", object () => throw new InvalidOperationException("veritabanı yok"));
        app.MapGet("/health", () => "ok");
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static HttpRequestMessage As(HttpMethod method, string path, string? user = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (user is not null)
            request.Headers.Add("X-Test-User", user);
        request.Headers.UserAgent.ParseAdd("CanTest/1.0");
        return request;
    }

    [Fact]
    public async Task Requests_are_recorded_with_route_user_status_and_masked_query()
    {
        var recorder = new Recorder();
        (WebApplication app, HttpClient client) = await CreateAsync(recorder);
        await using (app)
        {
            await client.SendAsync(As(HttpMethod.Get, "/api/orders/42?page=2&reset_token=gizli", "u1"), TestContext.Current.CancellationToken);
            await client.SendAsync(As(HttpMethod.Get, "/health"), TestContext.Current.CancellationToken); // hariç

            RequestActivity record = Assert.Single(await recorder.WaitForAsync(1));
            Assert.Equal("GET", record.Method);
            Assert.Equal("/api/orders/42", record.Path);
            Assert.Equal("/api/orders/{id:int}", record.Route);
            Assert.Equal("?page=2&reset_token=***", record.Query);
            Assert.Equal(200, record.StatusCode);
            Assert.Equal("u1", record.UserId);
            Assert.Equal("CanTest/1.0", record.UserAgent);
            Assert.False(string.IsNullOrEmpty(record.TraceId));
            Assert.True(record.DurationMs >= 0);
        }
    }

    [Fact]
    public async Task Unhandled_errors_are_recorded_as_500()
    {
        var recorder = new Recorder();
        (WebApplication app, HttpClient client) = await CreateAsync(recorder);
        await using (app)
        {
            HttpResponseMessage response = await client.SendAsync(As(HttpMethod.Get, "/api/fail", "u1"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.Equal(500, Assert.Single(await recorder.WaitForAsync(1)).StatusCode);
        }
    }

    [Fact]
    public async Task Reads_and_anonymous_requests_can_be_left_out()
    {
        var recorder = new Recorder();
        (WebApplication app, HttpClient client) = await CreateAsync(recorder, o => { o.IncludeReads = false; o.IncludeAnonymous = false; });
        await using (app)
        {
            await client.SendAsync(As(HttpMethod.Get, "/api/orders/1", "u1"), TestContext.Current.CancellationToken);   // okuma
            await client.SendAsync(As(HttpMethod.Post, "/api/orders"), TestContext.Current.CancellationToken);          // anonim
            await client.SendAsync(As(HttpMethod.Post, "/api/orders", "u2"), TestContext.Current.CancellationToken);    // kaydedilir

            await Task.Delay(300, TestContext.Current.CancellationToken);
            RequestActivity record = Assert.Single(await recorder.WaitForAsync(1));
            Assert.Equal("u2", record.UserId);
            Assert.Equal(201, record.StatusCode);
        }
    }

    [Fact]
    public async Task A_slow_sink_never_slows_requests_down()
    {
        var recorder = new Recorder(delay: TimeSpan.FromSeconds(1));
        (WebApplication app, HttpClient client) = await CreateAsync(recorder);
        await using (app)
        {
            var watch = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++)
                await client.SendAsync(As(HttpMethod.Get, $"/api/orders/{i}", "u1"), TestContext.Current.CancellationToken);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"20 istek {watch.Elapsed} sürdü");

            Assert.Equal(20, (await recorder.WaitForAsync(20)).Length); // sonra yazıldı
        }
    }

    [Fact]
    public async Task Pending_records_are_flushed_on_shutdown()
    {
        var recorder = new Recorder();
        (WebApplication app, HttpClient client) = await CreateAsync(recorder, o => o.FlushInterval = TimeSpan.FromMinutes(5));
        await client.SendAsync(As(HttpMethod.Post, "/api/orders", "u1"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken); // kayıt kuyrukta, yazma 5 dk sonra olacaktı

        await app.StopAsync(TestContext.Current.CancellationToken);
        await app.DisposeAsync();

        Assert.Single(recorder.Items);
    }
}
