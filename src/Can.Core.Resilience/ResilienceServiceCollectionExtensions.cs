using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Resilience;

/// <summary>
/// Adı verilmiş pipeline'lar. Her pipeline ilk istendiğinde bir kez kurulur ve paylaşılır (circuit breaker durumu
/// uygulama genelinde tektir).
/// </summary>
public sealed class ResiliencePipelineProvider
{
    private readonly IServiceProvider _services;
    private readonly ConcurrentDictionary<(Type, string), Lazy<object>> _pipelines = new();
    private readonly Dictionary<(Type, string), Func<IServiceProvider, object>> _factories;

    internal ResiliencePipelineProvider(IServiceProvider services, IEnumerable<PipelineRegistration> registrations)
    {
        _services = services;
        _factories = registrations.GroupBy(r => (r.ResultType, r.Name)).ToDictionary(g => g.Key, g => g.Last().Factory);
    }

    public ResiliencePipeline GetPipeline(string name) => (ResiliencePipeline)Get(typeof(void), name);

    public ResiliencePipeline<T> GetPipeline<T>(string name) => (ResiliencePipeline<T>)Get(typeof(T), name);

    private object Get(Type type, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_factories.TryGetValue((type, name), out Func<IServiceProvider, object>? factory))
            throw new KeyNotFoundException($"'{name}' adında bir resilience pipeline kayıtlı değil.");

        return _pipelines.GetOrAdd((type, name), _ => new Lazy<object>(() => factory(_services))).Value;
    }
}

internal sealed record PipelineRegistration(Type ResultType, string Name, Func<IServiceProvider, object> Factory);

public static class ResilienceServiceCollectionExtensions
{
    /// <summary>
    /// Adı verilmiş, paylaşılan pipeline kaydeder; <see cref="ResiliencePipelineProvider"/> ile alınır.
    /// </summary>
    /// <example>
    /// <code>
    /// services.AddCanResiliencePipeline("payments", (builder, sp) =&gt; builder
    ///     .AddRetry(new RetryOptions { MaxRetryAttempts = 3 })
    ///     .AddCircuitBreaker(new CircuitBreakerOptions { MinimumThroughput = 10 }));
    ///
    /// ResiliencePipeline pipeline = provider.GetPipeline("payments");
    /// </code>
    /// </example>
    public static IServiceCollection AddCanResiliencePipeline(
        this IServiceCollection services,
        string name,
        Action<ResiliencePipelineBuilder, IServiceProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddRegistration(typeof(void), name, sp =>
        {
            var builder = new ResiliencePipelineBuilder { TimeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System };
            configure(builder, sp);
            return builder.Build();
        });
    }

    public static IServiceCollection AddCanResiliencePipeline<T>(
        this IServiceCollection services,
        string name,
        Action<ResiliencePipelineBuilder<T>, IServiceProvider> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return services.AddRegistration(typeof(T), name, sp =>
        {
            var builder = new ResiliencePipelineBuilder<T> { TimeProvider = sp.GetService<TimeProvider>() ?? TimeProvider.System };
            configure(builder, sp);
            return builder.Build();
        });
    }

    private static IServiceCollection AddRegistration(this IServiceCollection services, Type type, string name, Func<IServiceProvider, object> factory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        services.AddSingleton(new PipelineRegistration(type, name, factory));
        services.TryAddSingleton(sp => new ResiliencePipelineProvider(sp, sp.GetServices<PipelineRegistration>()));
        return services;
    }
}
