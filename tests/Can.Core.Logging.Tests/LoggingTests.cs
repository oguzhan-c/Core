using System.Collections.Concurrent;
using System.Security.Claims;
using Can.Core.Logging.Serilog;
using Can.Core.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

namespace Can.Core.Logging.Tests;

public sealed class CollectingSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events => _events.ToArray();

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}

public class LoggingTests
{
    private static async Task<(WebApplication App, HttpClient Client, CollectingSink Sink)> CreateAsync()
    {
        var sink = new CollectingSink();

        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.AddCanSerilog(o =>
        {
            o.ApplicationName = "can-tests";
            o.ConfigureLogger = logger => logger.WriteTo.Sink(sink);
        });
        builder.Services.AddCanWebApi();

        WebApplication app = builder.Build();
        app.UseCanRequestLogging();
        app.UseCanExceptionHandler();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-User", out var user))
            {
                Claim[] claims = [new(ClaimTypes.NameIdentifier, user.ToString()), new("tenant_id", "11111111-1111-1111-1111-111111111111")];
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
            }

            await next(context);
        });
        app.UseCanTenantResolution();
        app.UseCanLogEnrichment();

        app.MapGet("/hello", (ILogger<LoggingTests> logger) =>
        {
            logger.LogInformation("Merhaba {Name}", "dünya");
            return "ok";
        });
        app.MapGet("/boom", () => { throw new InvalidOperationException("patladı"); });
        app.MapGet("/secret", () => { throw new Can.Core.Application.Exceptions.UnauthorizedException(); });

        await app.StartAsync();
        return (app, app.GetTestClient(), sink);
    }

    /// <summary>UseCanRequestLogging'in istek özeti (ASP.NET'in kendi logları da RequestPath taşır).</summary>
    private static LogEvent RequestSummary(CollectingSink sink, string path) =>
        sink.Events.Single(e => e.MessageTemplate.Text.StartsWith("HTTP ", StringComparison.Ordinal) && Scalar(e, "RequestPath") == path);

    private static string? Scalar(LogEvent logEvent, string property) =>
        logEvent.Properties.TryGetValue(property, out LogEventPropertyValue? value) && value is ScalarValue { Value: { } raw }
            ? raw.ToString()
            : null;

    [Fact]
    public async Task Logs_are_enriched_with_application_user_and_tenant()
    {
        (WebApplication app, HttpClient client, CollectingSink sink) = await CreateAsync();
        await using (app)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/hello");
            request.Headers.Add("X-Test-User", "u-42");
            await client.SendAsync(request);

            LogEvent hello = sink.Events.Single(e => e.MessageTemplate.Text == "Merhaba {Name}");
            Assert.Equal("can-tests", Scalar(hello, "Application"));
            Assert.Equal("u-42", Scalar(hello, "UserId"));
            Assert.Equal("11111111-1111-1111-1111-111111111111", Scalar(hello, "TenantId"));

            LogEvent summary = RequestSummary(sink, "/hello");
            Assert.Equal(LogEventLevel.Information, summary.Level);
            Assert.Equal("200", Scalar(summary, "StatusCode"));
            Assert.Equal("u-42", Scalar(summary, "UserId")); // özet satırında da kullanıcı var
        }
    }

    [Fact]
    public async Task Failed_requests_are_logged_as_errors()
    {
        (WebApplication app, HttpClient client, CollectingSink sink) = await CreateAsync();
        await using (app)
        {
            await client.GetAsync("/boom");

            LogEvent summary = RequestSummary(sink, "/boom");
            Assert.Equal(LogEventLevel.Error, summary.Level);
        }
    }

    [Fact]
    public async Task Handled_client_errors_are_logged_with_their_real_status()
    {
        (WebApplication app, HttpClient client, CollectingSink sink) = await CreateAsync();
        await using (app)
        {
            HttpResponseMessage response = await client.GetAsync("/secret");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);

            // Hata işleyici 401'e çevirdi: özet 500/Error değil 401/Warning olmalı.
            LogEvent summary = RequestSummary(sink, "/secret");
            Assert.Equal(LogEventLevel.Warning, summary.Level);
            Assert.Equal("401", Scalar(summary, "StatusCode"));
            Assert.Null(summary.Exception);
        }
    }
}
