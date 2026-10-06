using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Can.Core.Caching.Redis;

/// <summary>Dağıtık önbelleğe kısa ömürlü bir değer yazıp geri okur (Redis erişilebilir mi).</summary>
public sealed class DistributedCacheHealthCheck : IHealthCheck
{
    private static readonly DistributedCacheEntryOptions Entry = new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) };

    private readonly IDistributedCache _cache;

    public DistributedCacheHealthCheck(IDistributedCache cache) => _cache = cache;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string key = $"health:{Environment.MachineName}:{Guid.NewGuid():N}";
        byte[] value = [1];

        await _cache.SetAsync(key, value, Entry, cancellationToken).ConfigureAwait(false);
        byte[]? read = await _cache.GetAsync(key, cancellationToken).ConfigureAwait(false);
        await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);

        return read is [1] ? HealthCheckResult.Healthy() : new HealthCheckResult(context.Registration.FailureStatus, "Yazılan değer geri okunamadı.");
    }
}

public static class RedisHealthCheckExtensions
{
    /// <summary>
    /// Redis kontrolü ekler. Varsayılan durum <c>Degraded</c>: Redis yoksa HybridCache bellekle çalışmaya devam eder.
    /// </summary>
    public static IHealthChecksBuilder AddCanRedisCheck(
        this IHealthChecksBuilder builder,
        string name = "redis",
        HealthStatus failureStatus = HealthStatus.Degraded,
        params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<DistributedCacheHealthCheck>(name, failureStatus, tags.Length > 0 ? tags : new[] { "ready" }, TimeSpan.FromSeconds(5));
    }
}
