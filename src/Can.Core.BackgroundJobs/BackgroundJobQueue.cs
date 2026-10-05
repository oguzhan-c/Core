using System.Threading.Channels;
using Can.Core.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.BackgroundJobs;

/// <summary>Kuyruktaki bir iş: hangi tenant adına, ne çalıştırılacak.</summary>
internal sealed record QueuedJob(string Name, string? TenantId, TenantInfo? Tenant, Func<IServiceProvider, CancellationToken, Task> Execute);

/// <summary>Uygulama genelinde tek kanal (singleton).</summary>
internal sealed class BackgroundJobChannel
{
    public BackgroundJobChannel(BackgroundJobOptions options)
    {
        Channel = System.Threading.Channels.Channel.CreateBounded<QueuedJob>(
            new BoundedChannelOptions(Math.Max(1, options.QueueCapacity))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = options.WorkerCount <= 1,
            }
        );
    }

    public Channel<QueuedJob> Channel { get; }
}

/// <summary>Scoped: kuyruğa atan isteğin tenant'ını yakalar.</summary>
internal sealed class BackgroundJobQueue : IBackgroundJobQueue
{
    private readonly BackgroundJobChannel _channel;
    private readonly TenantContext? _tenantContext;

    public BackgroundJobQueue(BackgroundJobChannel channel, IServiceProvider serviceProvider)
    {
        _channel = channel;
        _tenantContext = serviceProvider.GetService<TenantContext>();
    }

    public ValueTask EnqueueAsync<TJob>(CancellationToken cancellationToken = default)
        where TJob : class, IBackgroundJob =>
        WriteAsync(typeof(TJob).Name, (services, ct) => services.GetRequiredService<TJob>().ExecuteAsync(ct), cancellationToken);

    public ValueTask EnqueueAsync<TJob, TArgs>(TArgs args, CancellationToken cancellationToken = default)
        where TJob : class, IBackgroundJob<TArgs> =>
        WriteAsync(typeof(TJob).Name, (services, ct) => services.GetRequiredService<TJob>().ExecuteAsync(args, ct), cancellationToken);

    private ValueTask WriteAsync(string name, Func<IServiceProvider, CancellationToken, Task> execute, CancellationToken cancellationToken) =>
        _channel.Channel.Writer.WriteAsync(new QueuedJob(name, _tenantContext?.TenantId, _tenantContext?.Tenant, execute), cancellationToken);
}
