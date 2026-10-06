using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Caching.Redis;

public sealed class CanRedisCacheOptions
{
    /// <summary>
    /// StackExchange.Redis bağlantı metni, ör. <c>"localhost:6379,abortConnect=false"</c>. Şifre içeriyorsa
    /// appsettings yerine ortam değişkeni ya da secret store'dan ver.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Anahtarların ön eki. Aynı Redis'i paylaşan uygulamalar çakışmasın diye uygulamaya özel olmalı (ör. <c>"northwind:"</c>).
    /// </summary>
    public string InstanceName { get; set; } = "can:";
}

public static class RedisCacheServiceCollectionExtensions
{
    /// <summary>
    /// Redis'i dağıtık önbellek (<c>IDistributedCache</c>) olarak kaydeder. <c>AddCanApplication</c>'ın kurduğu HybridCache
    /// bunu kendiliğinden ikinci katman olarak kullanır:
    /// <list type="bullet">
    /// <item>Okuma önce sunucunun belleğinden (L1), yoksa Redis'ten (L2), o da yoksa handler'dan.</item>
    /// <item>Yazılan değer hem belleğe hem Redis'e gider; diğer sunucular Redis'ten okur.</item>
    /// <item><c>ICacheRemoverRequest</c> etiket silmeleri Redis'e de yazılır; diğer sunucular eski kaydı kullanmaz.</item>
    /// </list>
    /// Kodda değişiklik gerekmez: <c>ICachableRequest</c> aynen çalışır.
    /// </summary>
    /// <example>
    /// <code>
    /// if (builder.Configuration["Redis:ConnectionString"] is { Length: &gt; 0 } redis)
    ///     builder.Services.AddCanRedisCache(o =&gt; { o.ConnectionString = redis; o.InstanceName = "northwind:"; });
    /// </code>
    /// </example>
    public static IServiceCollection AddCanRedisCache(this IServiceCollection services, Action<CanRedisCacheOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CanRedisCacheOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("Redis için ConnectionString verilmeli.");

        services.AddStackExchangeRedisCache(redis =>
        {
            redis.Configuration = options.ConnectionString;
            redis.InstanceName = options.InstanceName;
        });

        return services;
    }
}
