using Can.Core.Caching.Redis;
using Can.Core.Redis.ScaleOut;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CanAppTemplate.Infrastructure.Caching;

/// <summary>Redis: <c>Redis:ConnectionString</c> doluysa çok sunuculu kurulum.</summary>
public static class RedisServiceRegistration
{
    /// <summary>
    /// Önbellek, Data Protection anahtarları, hız sınırı, dağıtık kilit ve çıktı önbelleği Redis'e taşınır.
    /// Bağlantı dizesi boşsa hiçbir şey kaydedilmez (tek sunucu, bellek içi).
    /// </summary>
    /// <returns>Bağlantı dizesi; kapalıysa <see langword="null"/>.</returns>
    public static string? AddRedis(this IServiceCollection services, IConfiguration config)
    {
        string? redis = config["Redis:ConnectionString"] is { Length: > 0 } value ? value : null;
        if (redis is null)
            return null;

        services.AddCanRedisCache(o =>
        {
            o.ConnectionString = redis;
            o.InstanceName = "canapptemplate:";
        });

        services.AddCanRedisScaleOut(o =>
            {
                o.ConnectionString = redis;
                o.ApplicationName = "canapptemplate";
            })
            .PersistDataProtectionKeys()
            .UseForRateLimiting()
            .UseForDistributedLocks()
            .UseForOutputCache();

        return redis;
    }
}
