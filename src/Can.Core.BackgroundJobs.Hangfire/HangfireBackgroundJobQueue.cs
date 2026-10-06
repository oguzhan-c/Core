using Can.Core.MultiTenancy;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.BackgroundJobs.Hangfire;

/// <summary>
/// <see cref="IBackgroundJobQueue"/>'nun Hangfire implementasyonu (scoped): işi, kuyruğa atan isteğin tenant'ıyla
/// birlikte Hangfire depolamasına yazar. Uygulama kapansa da iş kaybolmaz; hata alırsa Hangfire yeniden dener.
/// </summary>
internal sealed class HangfireBackgroundJobQueue : IBackgroundJobQueue
{
    private readonly IBackgroundJobClient _client;
    private readonly TenantContext? _tenantContext;

    public HangfireBackgroundJobQueue(IBackgroundJobClient client, IServiceProvider services)
    {
        _client = client;
        _tenantContext = services.GetService<TenantContext>();
    }

    public ValueTask EnqueueAsync<TJob>(CancellationToken cancellationToken = default)
        where TJob : class, IBackgroundJob
    {
        cancellationToken.ThrowIfCancellationRequested();
        string jobName = typeof(TJob).Name;
        string? tenantId = _tenantContext?.TenantId;

        _client.Enqueue<HangfireJobRunner<TJob>>(r => r.RunAsync(jobName, tenantId, CancellationToken.None));
        return ValueTask.CompletedTask;
    }

    public ValueTask EnqueueAsync<TJob, TArgs>(TArgs args, CancellationToken cancellationToken = default)
        where TJob : class, IBackgroundJob<TArgs>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string jobName = typeof(TJob).Name;
        string? tenantId = _tenantContext?.TenantId;

        _client.Enqueue<HangfireJobRunner<TJob, TArgs>>(r => r.RunAsync(jobName, tenantId, args, CancellationToken.None));
        return ValueTask.CompletedTask;
    }
}
