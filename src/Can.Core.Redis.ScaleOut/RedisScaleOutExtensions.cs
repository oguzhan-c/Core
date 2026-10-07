using Can.Core.Application;
using Can.Core.WebApi.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Can.Core.Redis.ScaleOut;

/// <summary>Hangi özelliklerin Redis'e taşınacağı.</summary>
public sealed class RedisScaleOutBuilder
{
    internal RedisScaleOutBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    /// <summary>
    /// Data Protection anahtarları Redis'te: bir sunucunun şifrelediği cookie'yi (2FA, OAuth dönüşü, passkey) diğeri
    /// çözebilir. Anahtarlar 90 günde bir kendiliğinden yenilenir; eskiler okunabilir kalır.
    /// </summary>
    public RedisScaleOutBuilder PersistDataProtectionKeys()
    {
        Services.AddDataProtection().SetApplicationName(ApplicationName());
        Services
            .AddOptions<KeyManagementOptions>()
            .Configure<RedisConnection>((o, connection) => o.XmlRepository = new RedisXmlRepository(connection, connection.Key("data-protection-keys")));
        return this;
    }

    /// <summary>Hız sınırları (<c>AddCanRateLimiting</c>) sunucuların hepsinde ortak sayılır.</summary>
    public RedisScaleOutBuilder UseForRateLimiting()
    {
        Services.RemoveAll<IDistributedRateLimiterFactory>();
        Services.AddSingleton<IDistributedRateLimiterFactory>(sp => new RedisRateLimiterFactory(
            sp.GetRequiredService<RedisConnection>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<ILogger<RedisRateLimiterFactory>>()));
        return this;
    }

    /// <summary><see cref="IDistributedLock"/> sunucular arası çalışır.</summary>
    public RedisScaleOutBuilder UseForDistributedLocks()
    {
        Services.RemoveAll<IDistributedLock>();
        Services.AddSingleton<IDistributedLock>(sp => new RedisDistributedLock(sp.GetRequiredService<RedisConnection>(), sp.GetService<TimeProvider>() ?? TimeProvider.System));
        return this;
    }

    private string ApplicationName() =>
        (Services.First(d => d.ServiceType == typeof(RedisScaleOutOptions)).ImplementationInstance as RedisScaleOutOptions)!.ApplicationName;
}

public static class RedisScaleOutExtensions
{
    /// <summary>
    /// Uygulamayı birden çok sunucuda çalıştırmak için Redis. Tek bağlantı paylaşılır; özellikler tek tek açılır.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanRedisScaleOut(o =&gt; { o.ConnectionString = "localhost:6379"; o.ApplicationName = "northwind"; })
    ///     .PersistDataProtectionKeys()
    ///     .UseForRateLimiting()
    ///     .UseForDistributedLocks();
    /// builder.Services.AddCanSignalR().AddCanRedisBackplane();
    /// </code>
    /// </example>
    public static RedisScaleOutBuilder AddCanRedisScaleOut(this IServiceCollection services, Action<RedisScaleOutOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new RedisScaleOutOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("Redis için ConnectionString verilmeli.");
        if (string.IsNullOrWhiteSpace(options.ApplicationName))
            throw new InvalidOperationException("Redis için ApplicationName verilmeli.");

        services.RemoveAll<RedisScaleOutOptions>();
        services.AddSingleton(options);
        services.TryAddSingleton<RedisConnection>();
        return new RedisScaleOutBuilder(services);
    }

    /// <summary>
    /// SignalR backplane: bir sunucudan gönderilen bildirim, kullanıcı hangi sunucuya bağlıysa ona ulaşır.
    /// <c>AddCanRedisScaleOut</c>'un bağlantısını kullanır. (Microsoft'un SignalR Redis paketi.)
    /// </summary>
    public static ISignalRServerBuilder AddCanRedisBackplane(this ISignalRServerBuilder signalR)
    {
        ArgumentNullException.ThrowIfNull(signalR);
        signalR.AddStackExchangeRedis();
        signalR.Services
            .AddOptions<RedisOptions>()
            .Configure<RedisConnection>((o, connection) =>
            {
                o.Configuration.ChannelPrefix = RedisChannel.Literal(connection.Options.ApplicationName);
                o.ConnectionFactory = async _ => await connection.GetAsync().ConfigureAwait(false);
            });
        return signalR;
    }
}
