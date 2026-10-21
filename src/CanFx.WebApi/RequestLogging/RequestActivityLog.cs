using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Threading.Channels;
using CanFx.Application;
using CanFx.MultiTenancy;
using CanFx.WebApi.DependencyInjection;
using CanFx.WebApi.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CanFx.WebApi.RequestLogging;

/// <summary><c>AddCanRequestActivityLog(...)</c> ayarları.</summary>
public sealed class RequestActivityOptions
{
    /// <summary>
    /// Kaydedilmeyen yollar (ön ek, büyük/küçük harf duyarsız). Varsayılan: sağlık, metrik ve belge uç noktaları.
    /// </summary>
    public IList<string> ExcludedPaths { get; } = ["/health", "/metrics", "/openapi", "/docs"];

    /// <summary>Değeri <c>***</c> ile maskelenen sorgu parametreleri (büyük/küçük harf duyarsız; adında geçmesi yeterli).</summary>
    public IList<string> MaskedQueryKeys { get; } = ["token", "password", "secret", "code", "key", "signature", "sig", "otp"];

    /// <summary>Giriş yapmamış isteklerin de kaydı tutulur.</summary>
    public bool IncludeAnonymous { get; set; } = true;

    /// <summary>Okuma istekleri (GET, HEAD) de kaydedilir. Kapatılırsa yalnızca veri değiştirenler.</summary>
    public bool IncludeReads { get; set; } = true;

    /// <summary>Ek filtre: <see langword="false"/> dönen istek kaydedilmez.</summary>
    public Func<HttpContext, bool>? Filter { get; set; }

    /// <summary>Kayıtların loga da yazılması (kategori <c>CanFx.WebApi.RequestActivity</c>).</summary>
    public bool WriteToLog { get; set; }

    /// <summary>
    /// Bellekteki kuyruğun kapasitesi. Doluysa (yazma yavaş) en eski kayıt düşer; istek ASLA beklemez. Düşenler
    /// <c>can.activity.dropped</c> metriğiyle sayılır.
    /// </summary>
    public int Capacity { get; set; } = 10_000;

    /// <summary>Bir partide en çok kayıt.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Parti dolmasa da bu sürede bir yazılır.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

    internal void Validate()
    {
        var errors = new List<string>();
        if (Capacity <= 0)
            errors.Add("Capacity sıfırdan büyük olmalı.");
        if (BatchSize <= 0)
            errors.Add("BatchSize sıfırdan büyük olmalı.");
        if (FlushInterval <= TimeSpan.Zero)
            errors.Add("FlushInterval sıfırdan büyük olmalı.");
        if (errors.Count > 0)
            throw new InvalidOperationException("RequestActivityOptions geçersiz:\n- " + string.Join("\n- ", errors));
    }
}

/// <summary>Kayıtların bellekteki kuyruğu (middleware yazar, arka plan servisi okur).</summary>
internal sealed class RequestActivityQueue
{
    internal static readonly Meter Meter = new("CanFx.WebApi");

    private static readonly Counter<long> Dropped =
        Meter.CreateCounter<long>("can.activity.dropped", unit: "{record}", description: "Kuyruk dolduğu için düşen istek kayıtları");

    public RequestActivityQueue(RequestActivityOptions options)
    {
        Channel = System.Threading.Channels.Channel.CreateBounded<RequestActivity>(
            new BoundedChannelOptions(options.Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Dropped.Add(1));
    }

    public Channel<RequestActivity> Channel { get; }
}

/// <summary>İsteği ölçer ve kaydı kuyruğa bırakır (bekletmeden).</summary>
internal sealed class RequestActivityMiddleware(RequestDelegate next, RequestActivityOptions options, RequestActivityQueue queue)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsExcluded(context))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        DateTimeOffset started = DateTimeOffset.UtcNow;
        long timestamp = Stopwatch.GetTimestamp();
        int? failedStatus = null;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch
        {
            failedStatus = StatusCodes.Status500InternalServerError; // işlenmemiş hata: üst katman 500'e çevirir
            throw;
        }
        finally
        {
            Record(context, started, Stopwatch.GetElapsedTime(timestamp), failedStatus);
        }
    }

    private bool IsExcluded(HttpContext context)
    {
        if (!options.IncludeReads && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method)))
            return true;

        PathString path = context.Request.Path;
        foreach (string excluded in options.ExcludedPaths)
        {
            if (path.StartsWithSegments(excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void Record(HttpContext context, DateTimeOffset started, TimeSpan elapsed, int? failedStatus)
    {
        try
        {
            string? userId = context.User.Identity?.IsAuthenticated == true
                ? (context.User.FindFirst("sub") ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier))?.Value
                : null;
            if (userId is null && !options.IncludeAnonymous)
                return;
            if (options.Filter is { } filter && !filter(context))
                return;

            string userAgent = context.Request.Headers.UserAgent.ToString();
            var activity = new RequestActivity(
                started,
                context.Request.Method,
                Limit(context.Request.Path.Value ?? "/", 512)!,
                Limit((context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText, 512),
                MaskQuery(context.Request.Query),
                failedStatus ?? context.Response.StatusCode,
                Math.Round(elapsed.TotalMilliseconds, 2),
                userId,
                context.RequestServices.GetService<TenantContext>()?.TenantId,
                context.GetSessionId(),
                context.Connection.RemoteIpAddress?.ToString(),
                Limit(string.IsNullOrEmpty(userAgent) ? null : userAgent, 512),
                Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier);

            queue.Channel.Writer.TryWrite(activity); // dolu kuyrukta en eskisi düşer; istek beklemez
        }
        catch
        {
            // Kayıt tutulamadı diye istek etkilenmemeli.
        }
    }

    private string? MaskQuery(IQueryCollection query)
    {
        if (query.Count == 0)
            return null;

        var text = new StringBuilder("?");
        foreach ((string key, Microsoft.Extensions.Primitives.StringValues values) in query)
        {
            bool masked = options.MaskedQueryKeys.Any(k => key.Contains(k, StringComparison.OrdinalIgnoreCase));
            foreach (string? value in values)
            {
                if (text.Length > 1)
                    text.Append('&');
                text.Append(Uri.EscapeDataString(key)).Append('=').Append(masked ? "***" : Uri.EscapeDataString(value ?? string.Empty));
            }
        }

        return Limit(text.ToString(), 1024);
    }

    private static string? Limit(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];
}

/// <summary>Kuyruktaki kayıtları partiler halinde sink'lere yazar; kapanışta kalanları boşaltır.</summary>
internal sealed partial class RequestActivityWriter(
    RequestActivityQueue queue,
    RequestActivityOptions options,
    IEnumerable<IRequestActivitySink> sinks,
    ILogger<RequestActivityWriter> logger) : BackgroundService
{
    private readonly ILogger<RequestActivityWriter> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        ChannelReader<RequestActivity> reader = queue.Channel.Reader;
        var batch = new List<RequestActivity>(options.BatchSize);
        try
        {
            while (await reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                using var flushTimer = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                flushTimer.CancelAfter(options.FlushInterval);
                try
                {
                    while (batch.Count < options.BatchSize)
                    {
                        if (reader.TryRead(out RequestActivity? item))
                            batch.Add(item);
                        else if (!await reader.WaitToReadAsync(flushTimer.Token).ConfigureAwait(false))
                            break;
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // süre doldu: eldekini yaz
                }

                await FlushAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // kapanış: aşağıda kalanlar yazılır
        }

        while (reader.TryRead(out RequestActivity? item))
        {
            batch.Add(item);
            if (batch.Count >= options.BatchSize)
                await FlushAsync(batch, CancellationToken.None).ConfigureAwait(false);
        }

        await FlushAsync(batch, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FlushAsync(List<RequestActivity> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
            return;

        RequestActivity[] items = [.. batch];
        batch.Clear();
        foreach (IRequestActivitySink sink in sinks)
        {
            try
            {
                await sink.WriteAsync(items, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogSinkFailed(exception, sink.GetType().Name, items.Length);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "İstek kaydı sink'i {Sink} {Count} kaydı yazamadı.")]
    private partial void LogSinkFailed(Exception exception, string sink, int count);
}

/// <summary>Kayıtları yapılandırılmış log olarak yazar.</summary>
internal sealed partial class LoggingRequestActivitySink(ILoggerFactory loggerFactory) : IRequestActivitySink
{
    private readonly ILogger _logger = loggerFactory.CreateLogger("CanFx.WebApi.RequestActivity");

    public ValueTask WriteAsync(IReadOnlyList<RequestActivity> batch, CancellationToken cancellationToken = default)
    {
        foreach (RequestActivity a in batch)
            Log(a.Method, a.Route ?? a.Path, a.StatusCode, a.DurationMs, a.UserId ?? "-", a.TenantId ?? "-", a.IpAddress ?? "-", a.TraceId ?? "-");
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "İstek {Method} {Route} → {StatusCode} ({DurationMs} ms), kullanıcı {UserId}, tenant {TenantId}, IP {IpAddress}, iz {TraceId}")]
    private partial void Log(string method, string route, int statusCode, double durationMs, string userId, string tenantId, string ipAddress, string traceId);
}

public static class RequestActivityLogExtensions
{
    /// <summary>
    /// İstek aktivite logu: her isteğin kim/ne zaman/hangi tenant/hangi uç nokta/sonuç/süre/IP kaydı. <b>Varsayılan
    /// kapalıdır</b>; bu çağrı açar. Kayıtların nereye yazılacağı sink'lerle belirlenir: veritabanı
    /// (<c>AddCanRequestActivityStore&lt;TContext&gt;()</c>), log (<see cref="RequestActivityOptions.WriteToLog"/>) ya da
    /// kendi <see cref="IRequestActivitySink"/>'in. İstek kaydı yazmayı hiç beklemez.
    /// </summary>
    public static IServiceCollection AddCanRequestActivityLog(this IServiceCollection services, Action<RequestActivityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new RequestActivityOptions();
        configure?.Invoke(options);
        options.Validate();

        services.RemoveAll<RequestActivityOptions>();
        services.AddSingleton(options);
        services.TryAddSingleton<RequestActivityQueue>();
        services.AddHostedService<RequestActivityWriter>();
        if (options.WriteToLog)
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IRequestActivitySink, LoggingRequestActivitySink>());
        return services;
    }

    /// <summary>
    /// <c>UseAuthentication()</c> ve <c>UseCanTenantResolution()</c>'dan SONRA ekle (kullanıcı ve tenant bilinsin);
    /// <c>UseCanExceptionHandler()</c> bunun dışında kalır, işlenmemiş hatalar 500 olarak kaydedilir.
    /// </summary>
    public static IApplicationBuilder UseCanRequestActivityLog(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        StartupValidation.Require<RequestActivityOptions>(app, "UseCanRequestActivityLog", "builder.Services.AddCanRequestActivityLog()");
        return app.UseMiddleware<RequestActivityMiddleware>();
    }
}
