using Can.Core.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Context;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Can.Core.Logging.Serilog;

public sealed class CanLoggingOptions
{
    /// <summary>Her log satırına <c>Application</c> olarak eklenir. Boşsa ortamın uygulama adı kullanılır.</summary>
    public string? ApplicationName { get; set; }

    /// <summary>
    /// Konsol biçimi. <see langword="null"/>: Development'ta okunur metin, diğer ortamlarda JSON
    /// (Seq, Elastic, Loki, CloudWatch gibi sistemler JSON'u doğrudan okur).
    /// </summary>
    public bool? JsonConsole { get; set; }

    /// <summary>Doluysa günlük dönen JSON log dosyaları yazılır, ör. <c>"logs/app-.json"</c> (14 gün saklanır).</summary>
    public string? FilePath { get; set; }

    /// <summary>Ek yapılandırma (ör. kod ile sink eklemek).</summary>
    public Action<LoggerConfiguration>? ConfigureLogger { get; set; }
}

public static class SerilogExtensions
{
    /// <summary>
    /// Serilog'u uygulamanın tek log sağlayıcısı yapar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Varsayılanlar: seviye Information; Microsoft/System kaynakları Warning (istek ve SQL gürültüsü olmasın);
    /// <c>Application</c> ve <c>Environment</c> özellikleri; LogContext'ten gelen özellikler.
    /// </para>
    /// <para>
    /// appsettings'teki <c>"Serilog"</c> bölümü her şeyi ezebilir. <c>"Serilog:WriteTo"</c> tanımlıysa varsayılan konsol
    /// eklenmez; örneğin Seq için (<c>Serilog.Sinks.Seq</c> paketini ekleyip):
    /// <code>
    /// "Serilog": {
    ///   "MinimumLevel": { "Default": "Information" },
    ///   "WriteTo": [ { "Name": "Console" }, { "Name": "Seq", "Args": { "serverUrl": "http://localhost:5341" } } ]
    /// }
    /// </code>
    /// </para>
    /// </remarks>
    public static IHostApplicationBuilder AddCanSerilog(this IHostApplicationBuilder builder, Action<CanLoggingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new CanLoggingOptions();
        configure?.Invoke(options);

        IConfiguration configuration = builder.Configuration;
        IHostEnvironment environment = builder.Environment;
        string applicationName = options.ApplicationName ?? environment.ApplicationName;
        bool jsonConsole = options.JsonConsole ?? !environment.IsDevelopment();
        bool sinksConfigured = configuration.GetSection("Serilog:WriteTo").GetChildren().Any();

        builder.Services.AddSerilog(
            (services, logger) =>
            {
                logger
                    .MinimumLevel.Information()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
                    .MinimumLevel.Override("System", LogEventLevel.Warning)
                    .Enrich.FromLogContext()
                    .Enrich.WithProperty("Application", applicationName)
                    .Enrich.WithProperty("Environment", environment.EnvironmentName)
                    .ReadFrom.Configuration(configuration)
                    .ReadFrom.Services(services);

                if (!sinksConfigured)
                {
                    if (jsonConsole)
                        logger.WriteTo.Console(new RenderedCompactJsonFormatter());
                    else
                        logger.WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
                }

                if (!string.IsNullOrWhiteSpace(options.FilePath))
                {
                    logger.WriteTo.File(
                        new CompactJsonFormatter(),
                        options.FilePath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 14
                    );
                }

                options.ConfigureLogger?.Invoke(logger);
            }
        );

        return builder;
    }

    /// <summary>
    /// Her isteğin özetini tek satırda loglar (yöntem, yol, durum kodu, süre) ve istek boyunca yazılan tüm loglara
    /// <c>UserId</c> ile <c>TenantId</c> ekler. <c>UseAuthentication()</c> ve <c>UseCanTenantResolution()</c>'dan
    /// SONRA ekle. İstek/yanıt gövdeleri loglanmaz.
    /// </summary>
    public static IApplicationBuilder UseCanRequestLogging(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<LogContextEnrichmentMiddleware>();

        return app.UseSerilogRequestLogging(options =>
        {
            options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} yanıt {StatusCode} ({Elapsed:0} ms)";

            options.GetLevel = (httpContext, _, exception) =>
                exception is not null || httpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError
                    ? LogEventLevel.Error
                    : httpContext.Response.StatusCode >= StatusCodes.Status400BadRequest
                        ? LogEventLevel.Warning
                        : LogEventLevel.Information;

            options.EnrichDiagnosticContext = (diagnostics, httpContext) =>
            {
                if (httpContext.Connection.RemoteIpAddress is { } ip)
                    diagnostics.Set("ClientIp", ip.ToString());
            };
        });
    }
}

/// <summary>İstek boyunca yazılan her log satırına kullanıcı ve tenant bilgisini ekler.</summary>
internal sealed class LogContextEnrichmentMiddleware
{
    private readonly RequestDelegate _next;

    public LogContextEnrichmentMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string? userId = context.RequestServices.GetService<ICurrentUser>()?.Id;
        string? tenantId = context.RequestServices.GetService<ICurrentTenant>()?.Id?.ToString();

        using (LogContext.PushProperty("UserId", userId))
        using (LogContext.PushProperty("TenantId", tenantId))
        {
            await _next(context).ConfigureAwait(false);
        }
    }
}
