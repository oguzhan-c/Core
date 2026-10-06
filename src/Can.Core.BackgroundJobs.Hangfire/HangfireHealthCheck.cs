using Hangfire;
using Hangfire.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Can.Core.BackgroundJobs.Hangfire;

/// <summary>Hangfire depolamasına erişilebiliyor mu ve en az bir sunucu (worker) çalışıyor mu.</summary>
public sealed class HangfireHealthCheck : IHealthCheck
{
    private readonly JobStorage _storage;

    public HangfireHealthCheck(JobStorage storage) => _storage = storage;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        IMonitoringApi monitoring = _storage.GetMonitoringApi();
        int servers = monitoring.Servers().Count;
        long failed = monitoring.FailedCount();

        var data = new Dictionary<string, object> { ["servers"] = servers, ["failed"] = failed };

        return Task.FromResult(
            servers > 0
                ? HealthCheckResult.Healthy(data: data)
                : new HealthCheckResult(context.Registration.FailureStatus, "Çalışan Hangfire sunucusu yok.", data: data)
        );
    }
}

public static class HangfireHealthCheckExtensions
{
    /// <summary>Hangfire kontrolü ekler (varsayılan <c>Degraded</c>: işler bekler ama API çalışır).</summary>
    public static IHealthChecksBuilder AddCanHangfireCheck(
        this IHealthChecksBuilder builder,
        string name = "hangfire",
        HealthStatus failureStatus = HealthStatus.Degraded,
        params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<HangfireHealthCheck>(name, failureStatus, tags.Length > 0 ? tags : new[] { "ready" }, TimeSpan.FromSeconds(5));
    }
}
