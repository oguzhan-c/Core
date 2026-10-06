using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Can.Core.WebApi.HealthChecks;

public static class HealthCheckEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>
    /// Sağlık uç noktaları:
    /// <list type="bullet">
    /// <item><c>/health/live</c>: süreç ayakta mı (hiçbir kontrol çalışmaz; Kubernetes liveness).</item>
    /// <item><c>/health/ready</c>: <c>ready</c> etiketli kontroller (veritabanı, Redis ...); trafik alınabilir mi (readiness).</item>
    /// </list>
    /// <c>Healthy</c> ve <c>Degraded</c> 200, <c>Unhealthy</c> 503 döner. Yanıt JSON: genel durum, süre ve kontrol başına
    /// durum/süre/açıklama. Hata ayrıntısı (exception) yazılmaz.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddHealthChecks()
    ///     .AddCanDbContextCheck&lt;AppDbContext&gt;()
    ///     .AddCanRedisCheck();
    /// app.MapCanHealthChecks();   // yetkisiz erişilebilir; gerekiyorsa .RequireHost("*:8081") ile iç porta al
    /// </code>
    /// </example>
    public static IEndpointConventionBuilder MapCanHealthChecks(this IEndpointRouteBuilder endpoints, string basePath = "/health")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        RouteGroupBuilder group = endpoints.MapGroup(basePath).AllowAnonymous().DisableRateLimiting();

        group.MapHealthChecks("/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = WriteAsync });
        group.MapHealthChecks("/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"), ResponseWriter = WriteAsync });

        return group;
    }

    /// <summary>Kısa JSON rapor yazar.</summary>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";

        var body = new
        {
            status = report.Status.ToString(),
            durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new
                {
                    status = e.Value.Status.ToString(),
                    durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 1),
                    description = e.Value.Description ?? (e.Value.Exception is null ? null : "Kontrol başarısız."),
                    data = e.Value.Data.Count > 0 ? e.Value.Data : null,
                }
            ),
        };

        return JsonSerializer.SerializeAsync(context.Response.Body, body, Json, context.RequestAborted);
    }
}
