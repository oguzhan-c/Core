using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Resilience.Http;

/// <summary>İsteği pipeline içinden gönderen handler.</summary>
/// <remarks>
/// Tekrar denemede aynı <see cref="HttpRequestMessage"/> yeniden gönderilir: gövde tekrar okunabilir olmalı
/// (<c>StringContent</c>, <c>ByteArrayContent</c>, <c>JsonContent</c>); tek seferlik akışlarla retry kullanma.
/// </remarks>
public sealed class ResilienceHandler : DelegatingHandler
{
    /// <summary><see cref="ResilienceContext.Properties"/> içinde isteğin anahtarı.</summary>
    public const string RequestKey = "Can.Http.Request";

    private readonly ResiliencePipeline<HttpResponseMessage> _pipeline;

    public ResilienceHandler(ResiliencePipeline<HttpResponseMessage> pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _pipeline = pipeline;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var context = new ResilienceContext(cancellationToken, $"{request.Method} {request.RequestUri?.Host}");
        context.Properties[RequestKey] = request;

        Outcome<HttpResponseMessage> outcome = await _pipeline
            .ExecuteOutcomeAsync(async c => Outcome.FromResult(await base.SendAsync(request, c.CancellationToken).ConfigureAwait(false)), context)
            .ConfigureAwait(false);

        return outcome.GetResultOrRethrow();
    }
}

/// <summary>Standart HTTP dayanıklılık ayarları (Microsoft.Extensions.Http.Resilience'ın standart handler'ı ile aynı varsayılanlar).</summary>
public sealed class HttpStandardResilienceOptions
{
    /// <summary>Aynı anda en fazla istek (bulkhead); fazlası beklemeden reddedilir.</summary>
    public int MaxConcurrentRequests { get; set; } = 1000;

    /// <summary>Tüm denemeler dahil toplam süre.</summary>
    public TimeSpan TotalRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public RetryOptions<HttpResponseMessage> Retry { get; set; } = new()
    {
        MaxRetryAttempts = 3,
        Delay = TimeSpan.FromSeconds(2),
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        ShouldHandle = HttpPredicates.IsTransient,
        DelayGenerator = HttpPredicates.RetryAfterDelay,
    };

    public CircuitBreakerOptions<HttpResponseMessage> CircuitBreaker { get; set; } = new()
    {
        FailureRatio = 0.1,
        MinimumThroughput = 100,
        SamplingDuration = TimeSpan.FromSeconds(30),
        BreakDuration = TimeSpan.FromSeconds(5),
        ShouldHandle = HttpPredicates.IsTransient,
    };

    /// <summary>Tek bir denemenin süresi.</summary>
    public TimeSpan AttemptTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>HTTP için hazır "hata" tanımları.</summary>
public static class HttpPredicates
{
    /// <summary>
    /// Geçici hata: ağ hatası (<see cref="HttpRequestException"/>), deneme zaman aşımı, 5xx, 408 ve 429.
    /// 4xx (429 ve 408 hariç) tekrar denenmez: aynı istek yine aynı hatayı alır.
    /// </summary>
    public static bool IsTransient(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception switch
        {
            HttpRequestException or TimeoutRejectedException => true,
            null => outcome.Result is { } response && IsTransient(response.StatusCode),
            _ => false,
        };

    public static bool IsTransient(HttpStatusCode status) =>
        (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;

    /// <summary>Sunucu <c>Retry-After</c> verdiyse (saniye ya da tarih) o kadar bekle; vermediyse varsayılan hesap.</summary>
    public static TimeSpan? RetryAfterDelay(RetryDelayArguments<HttpResponseMessage> args)
    {
        if (args.Outcome.Result?.Headers.RetryAfter is not { } retryAfter)
            return null;

        if (retryAfter.Delta is { } delta)
            return delta;

        if (retryAfter.Date is { } date)
        {
            TimeSpan wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}

public static class HttpResilienceExtensions
{
    /// <summary>
    /// Standart dayanıklılık: bulkhead → toplam süre → retry (üstel + jitter, Retry-After'a uyar) → circuit breaker → deneme süresi.
    /// Pipeline bu HttpClient adı için bir kez kurulur ve paylaşılır.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddHttpClient&lt;PaymentClient&gt;().AddCanStandardResilienceHandler(o =&gt; o.Retry.MaxRetryAttempts = 5);
    /// </code>
    /// </example>
    public static IHttpClientBuilder AddCanStandardResilienceHandler(this IHttpClientBuilder builder, Action<HttpStandardResilienceOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new HttpStandardResilienceOptions();
        configure?.Invoke(options);

        return builder.AddCanResilienceHandler((pipeline, _) => pipeline
            .AddConcurrencyLimiter(options.MaxConcurrentRequests)
            .AddTimeout(options.TotalRequestTimeout)
            .AddRetry(options.Retry)
            .AddCircuitBreaker(options.CircuitBreaker)
            .AddTimeout(options.AttemptTimeout));
    }

    /// <summary>Kendi pipeline'ını kur.</summary>
    public static IHttpClientBuilder AddCanResilienceHandler(
        this IHttpClientBuilder builder,
        Action<ResiliencePipelineBuilder<HttpResponseMessage>, IServiceProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        // Handler'lar periyodik olarak yeniden oluşturulur; pipeline (ve circuit breaker durumu) kalıcı olmalı.
        object gate = new();
        ResiliencePipeline<HttpResponseMessage>? pipeline = null;

        return builder.AddHttpMessageHandler(sp =>
        {
            lock (gate)
            {
                if (pipeline is null)
                {
                    var pipelineBuilder = new ResiliencePipelineBuilder<HttpResponseMessage> { TimeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System };
                    configure(pipelineBuilder, sp);
                    pipeline = pipelineBuilder.Build();
                }
            }

            return new ResilienceHandler(pipeline);
        });
    }
}
