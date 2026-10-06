using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Can.Core.BackgroundJobs.Hangfire;

public sealed class CanHangfireOptions
{
    /// <summary>
    /// Bu uygulama işleri de çalıştırsın mı? Yalnızca iş kuyruğa atan (ör. API) ve işleri ayrı bir worker'da
    /// çalıştıran kurulumlarda kapat.
    /// </summary>
    public bool RunServer { get; set; } = true;

    /// <summary>Aynı anda çalışan iş sayısı. Boşsa Hangfire varsayılanı (işlemci sayısı × 5, en fazla 20).</summary>
    public int? WorkerCount { get; set; }

    /// <summary>Dashboard'daki sunucu adı. Boşsa makine adı + süreç kimliği.</summary>
    public string? ServerName { get; set; }

    /// <summary>Dinlenen kuyruklar (öncelik sırasıyla).</summary>
    public string[] Queues { get; set; } = ["default"];

    /// <summary>Zamanlanmış/tekrarlayan işlerin kontrol aralığı. Boşsa Hangfire varsayılanı (15 sn).</summary>
    public TimeSpan? SchedulePollingInterval { get; set; }
}

public static class HangfireServiceCollectionExtensions
{
    /// <summary>
    /// Arka plan işlerini Hangfire ile çalıştırır: <see cref="IBackgroundJobQueue"/> artık işleri bellekteki kanal
    /// yerine Hangfire depolamasına yazar (kalıcı, yeniden denenen, dashboard'da izlenen). İşler kuyruğa atıldıkları
    /// andaki tenant adına çalışır.
    /// </summary>
    /// <param name="services">Servis koleksiyonu.</param>
    /// <param name="configureStorage">Depolama seçimi (ör. <c>c.UsePostgreSqlStorage(...)</c>, testte <c>c.UseInMemoryStorage()</c>).</param>
    /// <param name="configure">Sunucu ayarları (işçi sayısı, kuyruklar, sunucu çalışsın mı).</param>
    /// <example>
    /// <code>
    /// builder.Services.AddCanBackgroundJobs(typeof(Program).Assembly);
    /// builder.Services.AddCanHangfire(c =&gt; c.UsePostgreSqlStorage(o =&gt; o.UseNpgsqlConnection(connectionString)));
    /// builder.Services.AddCanHangfireRecurringJob&lt;DailyReportJob&gt;("0 7 * * *", o =&gt; o.PerTenant = true);
    /// ...
    /// app.MapCanHangfireDashboard("/hangfire", "Admin");
    /// </code>
    /// </example>
    public static IServiceCollection AddCanHangfire(
        this IServiceCollection services,
        Action<IGlobalConfiguration> configureStorage,
        Action<CanHangfireOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureStorage);

        var options = new CanHangfireOptions();
        configure?.Invoke(options);

        services.AddHangfire(config =>
        {
            config
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings();
            configureStorage(config);
        });

        if (options.RunServer)
        {
            services.AddHangfireServer(server =>
            {
                server.Queues = options.Queues;
                if (options.WorkerCount is { } workers)
                    server.WorkerCount = Math.Max(1, workers);
                if (!string.IsNullOrWhiteSpace(options.ServerName))
                    server.ServerName = options.ServerName;
                if (options.SchedulePollingInterval is { } polling)
                    server.SchedulePollingInterval = polling;
            });
        }

        services.RemoveAll<IBackgroundJobQueue>();
        services.AddScoped<IBackgroundJobQueue, HangfireBackgroundJobQueue>();
        services.TryAddScoped(typeof(HangfireJobRunner<>));
        services.TryAddScoped(typeof(HangfireJobRunner<,>));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HangfireRecurringJobRegistrar>());

        return services;
    }

    /// <summary>
    /// <typeparamref name="TJob"/>'u cron ifadesiyle çalışan Hangfire işi olarak kaydeder (ör. <c>"0 7 * * *"</c> her gün
    /// 07:00, <c>Cron.Hourly()</c>). Birden fazla sunucuda da yalnızca bir kez çalışır; dashboard'dan elle tetiklenebilir.
    /// </summary>
    public static IServiceCollection AddCanHangfireRecurringJob<TJob>(
        this IServiceCollection services,
        string cronExpression,
        Action<HangfireRecurringJobSchedule>? configure = null)
        where TJob : class, IBackgroundJob
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);

        var schedule = new HangfireRecurringJobSchedule();
        configure?.Invoke(schedule);

        services.TryAddScoped<TJob>();
        services.TryAddScoped<HangfireJobRunner<TJob>>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, HangfireRecurringJobRegistrar>());
        services.AddSingleton(HangfireRecurringJobFactory.Create<TJob>(cronExpression, schedule));

        return services;
    }
}
