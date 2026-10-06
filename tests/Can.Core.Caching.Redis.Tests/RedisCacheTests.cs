using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Can.Core.Caching.Redis.Tests;

public class RedisCacheTests
{
    [Fact]
    public void Redis_becomes_the_distributed_layer_of_hybrid_cache()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHybridCache();
        services.AddCanRedisCache(o =>
        {
            o.ConnectionString = "localhost:6379,abortConnect=false";
            o.InstanceName = "test:";
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        // Bağlantı ilk kullanımda kurulur; kayıt Redis sunucusu olmadan doğrulanabilir.
        Assert.IsAssignableFrom<RedisCache>(provider.GetRequiredService<IDistributedCache>());
        Assert.NotNull(provider.GetRequiredService<HybridCache>());

        RedisCacheOptions options = provider.GetRequiredService<IOptions<RedisCacheOptions>>().Value;
        Assert.Equal("test:", options.InstanceName);
        Assert.Equal("localhost:6379,abortConnect=false", options.Configuration);
    }

    [Fact]
    public void Connection_string_is_required()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddCanRedisCache(_ => { }));
    }
}
