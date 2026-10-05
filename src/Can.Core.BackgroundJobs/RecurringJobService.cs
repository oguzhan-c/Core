using Can.Core.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Can.Core.BackgroundJobs;

/// <summary><typeparamref name="TJob"/>'u <see cref="RecurringJobOptions.Interval"/> aralıklarla çalıştırır.</summary>
internal sealed partial class RecurringJobService<TJob> : BackgroundService
    where TJob : class, IBackgroundJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RecurringJobOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RecurringJobService<TJob>> _logger;

    public RecurringJobService(
        IServiceScopeFactory scopeFactory,
        RecurringJobOptions options,
        TimeProvider timeProvider,
        ILogger<RecurringJobService<TJob>> logger)
    {
        if (options.Interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), $"{typeof(TJob).Name} için Interval sıfırdan büyük olmalı.");

        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (_options.RunOnStartup)
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(_options.Interval, _timeProvider);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // uygulama kapanıyor
        }
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        if (!_options.PerTenant)
        {
            await RunAsync(tenant: null, stoppingToken).ConfigureAwait(false);
            return;
        }

        IReadOnlyList<TenantInfo> tenants;
        await using (AsyncServiceScope scope = _scopeFactory.CreateAsyncScope())
        {
            ITenantStore store =
                scope.ServiceProvider.GetService<ITenantStore>()
                ?? throw new InvalidOperationException(
                    $"{typeof(TJob).Name} tenant başına çalışacak şekilde ayarlanmış ama ITenantStore kayıtlı değil."
                );
            tenants = await store.GetAllAsync(stoppingToken).ConfigureAwait(false);
        }

        foreach (TenantInfo tenant in tenants.Where(t => t.IsActive))
            await RunAsync(tenant, stoppingToken).ConfigureAwait(false);
    }

    private async Task RunAsync(TenantInfo? tenant, CancellationToken stoppingToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            JobTenantScope.Apply(scope.ServiceProvider, tenant?.Id, tenant);
            await scope.ServiceProvider.GetRequiredService<TJob>().ExecuteAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Bir çalıştırmanın hatası sonrakileri durdurmaz.
            LogRunFailed(exception, typeof(TJob).Name, tenant?.Id);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Tekrarlayan iş {JobName} başarısız oldu (tenant: {TenantId}).")]
    private partial void LogRunFailed(Exception exception, string jobName, string? tenantId);
}
