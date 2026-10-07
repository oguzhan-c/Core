using System.Threading.RateLimiting;

namespace Can.Core.Resilience;

/// <summary>Pipeline kurucularının ortak kısmı. Stratejiler eklenme sırasıyla dıştan içe sarılır.</summary>
public abstract class ResiliencePipelineBuilderBase<TSelf, T>
    where TSelf : ResiliencePipelineBuilderBase<TSelf, T>
{
    private readonly List<Func<TimeProvider, IStrategyFactory>> _factories = [];

    /// <summary>Beklemeler ve süreler için saat (testlerde sahte saat verilebilir).</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Hiç strateji eklenmedi mi.</summary>
    public bool IsEmpty => _factories.Count == 0;

    /// <summary>Kendi stratejini ekle.</summary>
    public TSelf AddStrategy(Func<TimeProvider, ResilienceStrategy<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        return AddFactory(time => new CustomStrategyFactory<T>(factory(time)));
    }

    public TSelf AddRetry(RetryOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return AddFactory(time => new RetryStrategyFactory<T>(options, time));
    }

    public TSelf AddCircuitBreaker(CircuitBreakerOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return AddFactory(time => new CircuitBreakerStrategyFactory<T>(options, time));
    }

    public TSelf AddTimeout(TimeSpan timeout) => AddTimeout(new TimeoutOptions { Timeout = timeout });

    public TSelf AddTimeout(TimeoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return AddFactory(time => new TimeoutStrategyFactory(options, time));
    }

    /// <summary>Bulkhead: aynı anda en fazla <paramref name="permitLimit"/> iş, <paramref name="queueLimit"/> kadarı sıra bekler.</summary>
    public TSelf AddConcurrencyLimiter(int permitLimit, int queueLimit = 0) =>
        AddRateLimiter(new RateLimiterStrategyOptions { PermitLimit = permitLimit, QueueLimit = queueLimit });

    public TSelf AddRateLimiter(RateLimiter limiter) => AddRateLimiter(new RateLimiterStrategyOptions { RateLimiter = limiter });

    public TSelf AddRateLimiter(RateLimiterStrategyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return AddFactory(_ =>
        {
            RateLimiter limiter = options.RateLimiter ?? new ConcurrencyLimiter(
                new ConcurrencyLimiterOptions
                {
                    PermitLimit = options.PermitLimit,
                    QueueLimit = options.QueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                }
            );
            return new RateLimiterStrategyFactory(limiter, options);
        });
    }

    private protected TSelf AddFactory(Func<TimeProvider, IStrategyFactory> factory)
    {
        _factories.Add(factory);
        return (TSelf)this;
    }

    /// <summary>Fabrikaları (ve durumlu stratejilerin ortak durumunu) bir kez oluşturur.</summary>
    private protected IStrategyFactory[] CreateFactories() => _factories.Select(f => f(TimeProvider)).ToArray();
}

/// <summary>
/// Dönüş tipine bakabilen pipeline kurucusu (ör. <c>HttpResponseMessage</c>'ın durum koduna göre tekrar dene).
/// </summary>
/// <example>
/// <code>
/// ResiliencePipeline&lt;HttpResponseMessage&gt; pipeline = new ResiliencePipelineBuilder&lt;HttpResponseMessage&gt;()
///     .AddTimeout(TimeSpan.FromSeconds(30))
///     .AddRetry(new RetryOptions&lt;HttpResponseMessage&gt;
///     {
///         ShouldHandle = new PredicateBuilder&lt;HttpResponseMessage&gt;().Handle&lt;HttpRequestException&gt;().HandleResult(r =&gt; (int)r.StatusCode &gt;= 500),
///         BackoffType = DelayBackoffType.Exponential,
///         UseJitter = true,
///     })
///     .AddCircuitBreaker(new CircuitBreakerOptions&lt;HttpResponseMessage&gt; { MinimumThroughput = 20 })
///     .AddTimeout(TimeSpan.FromSeconds(5))   // deneme başına
///     .Build();
/// </code>
/// </example>
public sealed class ResiliencePipelineBuilder<T> : ResiliencePipelineBuilderBase<ResiliencePipelineBuilder<T>, T>
{
    public ResiliencePipelineBuilder<T> AddFallback(FallbackOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return AddFactory(_ => new TypedStrategyFactory<T>(new FallbackStrategy<T>(options)));
    }

    public ResiliencePipelineBuilder<T> AddHedging(HedgingOptions<T> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return AddFactory(time => new TypedStrategyFactory<T>(new HedgingStrategy<T>(options, time)));
    }

    /// <summary>Her çağrı yeni (durumu ayrı) bir pipeline üretir; durumlu stratejiler için tek örneği paylaş.</summary>
    public ResiliencePipeline<T> Build() => new(CreateFactories().Select(f => f.Create<T>()));
}

/// <summary>Dönüş tipinden bağımsız (yalnızca exception'lara bakan) pipeline kurucusu.</summary>
/// <example>
/// <code>
/// ResiliencePipeline pipeline = new ResiliencePipelineBuilder()
///     .AddRetry(new RetryOptions { MaxRetryAttempts = 5, BackoffType = DelayBackoffType.Exponential, UseJitter = true })
///     .AddTimeout(TimeSpan.FromSeconds(10))
///     .Build();
///
/// Order order = await pipeline.ExecuteAsync(ct =&gt; client.GetOrderAsync(id, ct), cancellationToken);
/// </code>
/// </example>
public sealed class ResiliencePipelineBuilder : ResiliencePipelineBuilderBase<ResiliencePipelineBuilder, object?>
{
    public ResiliencePipeline Build() => new(CreateFactories());
}

/// <summary>Yalnızca kendi tipinde çalışan strateji (fallback, hedging: tipli kurucuya özel).</summary>
internal sealed class TypedStrategyFactory<T>(ResilienceStrategy<T> strategy) : StrategyFactory<T>
{
    protected override ResilienceStrategy<T> CreateNative() => strategy;

    protected override ResilienceStrategy<TResult> CreateAdapted<TResult>() =>
        throw new NotSupportedException($"{strategy.GetType().Name} yalnızca {typeof(T).Name} sonuçlarıyla kullanılabilir.");
}
