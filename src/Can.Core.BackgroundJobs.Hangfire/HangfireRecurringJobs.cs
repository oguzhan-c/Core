using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using HangfireRecurringJobOptions = Hangfire.RecurringJobOptions;

namespace Can.Core.BackgroundJobs.Hangfire;

/// <summary>Cron ile çalışan Hangfire işinin ayarları.</summary>
public sealed class HangfireRecurringJobSchedule
{
    /// <summary>Dashboard'da görünen ve güncellemede kullanılan kimlik. Boşsa iş tipinin adı.</summary>
    public string? JobId { get; set; }

    /// <summary>
    /// Her aktif tenant için ayrı iş olarak çalıştır (<c>ITenantStore</c> gerekir). Kapalıysa iş tenant'sız (host) çalışır.
    /// </summary>
    public bool PerTenant { get; set; }

    /// <summary>Cron ifadesinin yorumlandığı saat dilimi. Varsayılan UTC.</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;
}

/// <summary>Uygulama açılırken Hangfire'a eklenecek tekrarlayan iş.</summary>
internal sealed record HangfireRecurringJobRegistration(string JobId, Action<IRecurringJobManager> Register);

/// <summary>Kayıtlı tekrarlayan işleri açılışta Hangfire'a ekler/günceller (<c>AddOrUpdate</c> idempotenttir).</summary>
internal sealed class HangfireRecurringJobRegistrar : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly IEnumerable<HangfireRecurringJobRegistration> _registrations;

    public HangfireRecurringJobRegistrar(IServiceProvider services, IEnumerable<HangfireRecurringJobRegistration> registrations)
    {
        _services = services;
        _registrations = registrations;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        IRecurringJobManager manager = _services.GetRequiredService<IRecurringJobManager>();
        foreach (HangfireRecurringJobRegistration registration in _registrations)
            registration.Register(manager);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal static class HangfireRecurringJobFactory
{
    public static HangfireRecurringJobRegistration Create<TJob>(string cronExpression, HangfireRecurringJobSchedule schedule)
        where TJob : class, IBackgroundJob
    {
        string jobName = typeof(TJob).Name;
        string jobId = string.IsNullOrWhiteSpace(schedule.JobId) ? jobName : schedule.JobId;
        var options = new HangfireRecurringJobOptions { TimeZone = schedule.TimeZone };

        return new HangfireRecurringJobRegistration(
            jobId,
            manager =>
            {
                if (schedule.PerTenant)
                    manager.AddOrUpdate<HangfireJobRunner<TJob>>(jobId, r => r.RunForEachTenantAsync(jobName, CancellationToken.None), cronExpression, options);
                else
                    manager.AddOrUpdate<HangfireJobRunner<TJob>>(jobId, r => r.RunAsync(jobName, null, CancellationToken.None), cronExpression, options);
            }
        );
    }
}
