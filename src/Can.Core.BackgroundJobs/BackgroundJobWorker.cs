using Can.Core.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Can.Core.BackgroundJobs;

/// <summary>Kuyruktaki işleri sırayla (ya da <see cref="BackgroundJobOptions.WorkerCount"/> kadar paralel) çalıştırır.</summary>
internal sealed partial class BackgroundJobWorker : BackgroundService
{
    private readonly BackgroundJobChannel _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackgroundJobOptions _options;
    private readonly ILogger<BackgroundJobWorker> _logger;

    public BackgroundJobWorker(
        BackgroundJobChannel channel,
        IServiceScopeFactory scopeFactory,
        BackgroundJobOptions options,
        ILogger<BackgroundJobWorker> logger)
    {
        _channel = channel;
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, Math.Max(1, _options.WorkerCount)).Select(_ => ConsumeAsync(stoppingToken)));

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (QueuedJob job in _channel.Channel.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
                await RunAsync(job, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // uygulama kapanıyor
        }
    }

    private async Task RunAsync(QueuedJob job, CancellationToken stoppingToken)
    {
        try
        {
            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            JobTenantScope.Apply(scope.ServiceProvider, job.TenantId, job.Tenant);
            await job.Execute(scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogJobFailed(exception, job.Name, job.TenantId);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Arka plan işi {JobName} başarısız oldu (tenant: {TenantId}).")]
    private partial void LogJobFailed(Exception exception, string jobName, string? tenantId);
}

internal static class JobTenantScope
{
    /// <summary>İşin scope'unu ilgili tenant adına ayarlar (çoklu kiracılık kayıtlıysa).</summary>
    public static void Apply(IServiceProvider services, string? tenantId, TenantInfo? tenant)
    {
        if (services.GetService<TenantContext>() is not { } context)
            return;

        if (tenant is not null)
            context.Set(tenant);
        else
            context.Set(tenantId);
    }
}
