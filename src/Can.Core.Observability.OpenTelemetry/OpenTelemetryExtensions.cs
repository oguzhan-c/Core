using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Can.Core.Observability.OpenTelemetry;

/// <summary>Can.Core paketlerinin telemetri kaynak adları (paketlere bağımlılık olmasın diye sabit).</summary>
public static class CanTelemetrySources
{
    /// <summary>Span (ActivitySource) adları.</summary>
    public static readonly IReadOnlyList<string> ActivitySources =
    [
        "Can.Core.Application",     // istekler (TelemetryBehavior)
        "Can.Core.Mediator",        // domain event yayını
        "Can.Core.BackgroundJobs",  // arka plan işleri (bellek içi + Hangfire)
        "Can.Core.Persistence",     // outbox
        "Can.Core.EventBus",        // yayın / işleme (traceparent ile servisler arası)
        "Can.Core.Mailing",         // e-posta gönderimi
        "Can.Core.Search",          // arama / dizinleme
        "Can.Core.Sms",             // SMS gönderimi
    ];

    /// <summary>Metrik (Meter) adları.</summary>
    public static readonly IReadOnlyList<string> Meters =
    [
        "Can.Core.Application",     // can.request.duration
        "Can.Core.BackgroundJobs",  // can.job.duration
        "Can.Core.Persistence",     // can.outbox.messages
        "Can.Core.Mailing",         // can.email.messages
        "Can.Core.Sms",             // can.sms.messages, can.sms.segments
        "Can.Core.Resilience",      // can.resilience.events
    ];
}

public sealed class CanOpenTelemetryOptions
{
    /// <summary>Servis adı (izlerde ve metriklerde <c>service.name</c>). Zorunlu.</summary>
    public string ServiceName { get; set; } = string.Empty;

    public string? ServiceVersion { get; set; }

    /// <summary>
    /// OTLP adresi (ör. <c>http://localhost:4317</c>). Boşsa <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> ortam değişkeni,
    /// o da yoksa <c>http://localhost:4317</c>.
    /// </summary>
    public Uri? OtlpEndpoint { get; set; }

    /// <summary>Kaydedilecek izlerin oranı (0–1). Üst servis karar verdiyse ona uyulur (parent-based).</summary>
    public double TraceSampleRatio { get; set; } = 1.0;

    /// <summary>ASP.NET Core'un yerleşik span ve metrikleri (istekler, Kestrel, routing, rate limiting).</summary>
    public bool IncludeAspNetCore { get; set; } = true;

    /// <summary>HttpClient'ın yerleşik span ve metrikleri (dış çağrılar).</summary>
    public bool IncludeHttpClient { get; set; } = true;

    /// <summary>Runtime metrikleri (GC, thread pool, bellek, exception sayısı).</summary>
    public bool IncludeRuntime { get; set; } = true;

    /// <summary>Ek span kaynakları (ör. <c>"Npgsql"</c>, <c>"Hangfire"</c>).</summary>
    public List<string> AdditionalSources { get; } = [];

    /// <summary>Ek metrikler (ör. <c>"Npgsql"</c>, <c>"Microsoft.EntityFrameworkCore"</c>).</summary>
    public List<string> AdditionalMeters { get; } = [];
}

public static class OpenTelemetryServiceCollectionExtensions
{
    /// <summary>
    /// İzleri (trace) ve metrikleri OTLP ile dışa aktarır. Instrumentation paketleri gerekmez: Can.Core paketleri ve
    /// .NET (ASP.NET Core, HttpClient, runtime) zaten span/metrik üretir; burada yalnızca dinlenir.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanOpenTelemetry(o =&gt;
    /// {
    ///     o.ServiceName = "northwind";
    ///     o.OtlpEndpoint = new Uri("http://localhost:4317");
    ///     o.AdditionalSources.Add("Npgsql");
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCanOpenTelemetry(this IServiceCollection services, Action<CanOpenTelemetryOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CanOpenTelemetryOptions();
        configure(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ServiceName);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.TraceSampleRatio, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.TraceSampleRatio, 1);

        string[] sources = [.. CanTelemetrySources.ActivitySources, .. BuiltInSources(options), .. options.AdditionalSources];
        string[] meters = [.. CanTelemetrySources.Meters, .. BuiltInMeters(options), .. options.AdditionalMeters];

        services
            .AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                options.ServiceName,
                serviceVersion: options.ServiceVersion,
                serviceInstanceId: Environment.MachineName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(sources)
                    .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(options.TraceSampleRatio)))
                    .AddOtlpExporter(otlp => Configure(otlp, options));
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(meters)
                    .AddOtlpExporter(otlp => Configure(otlp, options));
            });

        return services;
    }

    private static void Configure(global::OpenTelemetry.Exporter.OtlpExporterOptions otlp, CanOpenTelemetryOptions options)
    {
        if (options.OtlpEndpoint is not null)
            otlp.Endpoint = options.OtlpEndpoint;
    }

    private static IEnumerable<string> BuiltInSources(CanOpenTelemetryOptions options)
    {
        if (options.IncludeAspNetCore)
            yield return "Microsoft.AspNetCore";
        if (options.IncludeHttpClient)
            yield return "System.Net.Http";
    }

    private static IEnumerable<string> BuiltInMeters(CanOpenTelemetryOptions options)
    {
        if (options.IncludeAspNetCore)
        {
            yield return "Microsoft.AspNetCore.Hosting";
            yield return "Microsoft.AspNetCore.Server.Kestrel";
            yield return "Microsoft.AspNetCore.Routing";
            yield return "Microsoft.AspNetCore.Diagnostics";
            yield return "Microsoft.AspNetCore.RateLimiting";
        }

        if (options.IncludeHttpClient)
        {
            yield return "System.Net.Http";
            yield return "System.Net.NameResolution";
        }

        if (options.IncludeRuntime)
            yield return "System.Runtime";
    }
}
