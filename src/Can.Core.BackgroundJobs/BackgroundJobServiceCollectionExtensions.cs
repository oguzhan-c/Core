using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Can.Core.BackgroundJobs;

public static class BackgroundJobServiceCollectionExtensions
{
    /// <summary>
    /// <see cref="IBackgroundJobQueue"/>'yu ve kuyruğu işleyen hosted service'i kaydeder; verilen assembly'lerdeki
    /// <see cref="IBackgroundJob"/> / <see cref="IBackgroundJob{TArgs}"/> sınıflarını scoped olarak ekler.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanBackgroundJobs(typeof(Program).Assembly);
    /// builder.Services.AddCanRecurringJob&lt;CleanupExpiredTokensJob&gt;(o =&gt; o.Interval = TimeSpan.FromHours(1));
    /// </code>
    /// </example>
    public static IServiceCollection AddCanBackgroundJobs(
        this IServiceCollection services,
        Action<BackgroundJobOptions>? configure,
        params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(assemblies);

        var options = new BackgroundJobOptions();
        configure?.Invoke(options);

        services.RemoveAll<BackgroundJobOptions>();
        services.AddSingleton(options);
        AddCore(services);

        foreach (Type type in assemblies.Distinct().SelectMany(a => a.GetTypes()).Where(IsJobType))
            services.TryAddScoped(type);

        return services;
    }

    /// <inheritdoc cref="AddCanBackgroundJobs(IServiceCollection, Action{BackgroundJobOptions}?, Assembly[])"/>
    public static IServiceCollection AddCanBackgroundJobs(this IServiceCollection services, params Assembly[] assemblies) =>
        services.AddCanBackgroundJobs(configure: null, assemblies);

    /// <summary>
    /// <typeparamref name="TJob"/>'u belirli aralıklarla çalışan iş olarak kaydeder. Aynı uygulama birden fazla
    /// örnekle çalışıyorsa iş her örnekte çalışır; işin bunu tolere etmesi (ör. kayıtları kilitleyerek almak) gerekir.
    /// </summary>
    public static IServiceCollection AddCanRecurringJob<TJob>(this IServiceCollection services, Action<RecurringJobOptions>? configure = null)
        where TJob : class, IBackgroundJob
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new RecurringJobOptions();
        configure?.Invoke(options);

        AddCore(services);
        services.TryAddScoped<TJob>();
        services.AddSingleton<IHostedService>(sp => new RecurringJobService<TJob>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            options,
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<RecurringJobService<TJob>>>()
        ));

        return services;
    }

    private static void AddCore(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new BackgroundJobOptions());
        services.TryAddSingleton<BackgroundJobChannel>();
        services.TryAddScoped<IBackgroundJobQueue, BackgroundJobQueue>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, BackgroundJobWorker>());
    }

    private static bool IsJobType(Type type) =>
        type is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false }
        && type.GetInterfaces().Any(i => i == typeof(IBackgroundJob) || (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IBackgroundJob<>)));
}
