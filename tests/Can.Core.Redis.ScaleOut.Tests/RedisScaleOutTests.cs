using System.Threading.RateLimiting;
using Can.Core.Application;
using Can.Core.WebApi.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Can.Core.Redis.ScaleOut.Tests;

/// <summary>Redis olmadan: kayıtlar (bağlantı ilk kullanımda açıldığı için bağlanılmaz).</summary>
public class RedisScaleOutRegistrationTests
{
    [Fact]
    public void Features_are_opt_in()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCanRedisScaleOut(o => o.ConnectionString = "localhost:6379");

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Null(provider.GetService<IDistributedRateLimiterFactory>());
        Assert.Null(provider.GetService<IDistributedLock>());
    }

    [Fact]
    public void Builder_registers_redis_implementations()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCanRedisScaleOut(o =>
            {
                o.ConnectionString = "localhost:6379";
                o.ApplicationName = "northwind";
            })
            .PersistDataProtectionKeys()
            .UseForRateLimiting()
            .UseForDistributedLocks();

        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.IsType<RedisRateLimiterFactory>(provider.GetRequiredService<IDistributedRateLimiterFactory>());
        Assert.IsType<RedisDistributedLock>(provider.GetRequiredService<IDistributedLock>());
        Assert.Equal("northwind:lock:x", provider.GetRequiredService<RedisConnection>().Key("lock:x"));
    }

    [Theory]
    [InlineData("", "app")]
    [InlineData("localhost:6379", "")]
    public void Connection_string_and_application_name_are_required(string connection, string application)
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddCanRedisScaleOut(o =>
        {
            o.ConnectionString = connection;
            o.ApplicationName = application;
        }));
    }
}

/// <summary>
/// Gerçek Redis'e karşı: <c>CAN_REDIS_URL=localhost:6379</c> verilmezse atlanır (<c>docker compose up -d redis</c>).
/// "İki sunucu" iki ayrı bağlantı ve servis sağlayıcıyla canlandırılır.
/// </summary>
public class RedisScaleOutIntegrationTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("CAN_REDIS_URL");

    private static ServiceProvider Server(string application, Action<RedisScaleOutBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure(services.AddCanRedisScaleOut(o =>
        {
            o.ConnectionString = Url!;
            o.ApplicationName = application;
        }));
        return services.BuildServiceProvider();
    }

    private static string NewApplication() => $"can-test-{Guid.NewGuid():N}";

    [Fact]
    public async Task Data_protection_keys_are_shared_between_servers()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Url), "CAN_REDIS_URL verilmedi.");
        string application = NewApplication();

        await using ServiceProvider server1 = Server(application, b => b.PersistDataProtectionKeys());
        await using ServiceProvider server2 = Server(application, b => b.PersistDataProtectionKeys());

        string protectedText = server1.GetRequiredService<IDataProtectionProvider>().CreateProtector("2fa").Protect("bekleyen giriş");
        string text = server2.GetRequiredService<IDataProtectionProvider>().CreateProtector("2fa").Unprotect(protectedText);

        Assert.Equal("bekleyen giriş", text);
    }

    [Fact]
    public async Task Rate_limit_is_counted_across_servers()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Url), "CAN_REDIS_URL verilmedi.");
        string application = NewApplication();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var rule = new RateLimitRule { PermitLimit = 3, Window = TimeSpan.FromMinutes(1) };

        await using ServiceProvider server1 = Server(application, b => b.UseForRateLimiting());
        await using ServiceProvider server2 = Server(application, b => b.UseForRateLimiting());
        RateLimiter limiter1 = server1.GetRequiredService<IDistributedRateLimiterFactory>().Create("auth:1.2.3.4:-", rule);
        RateLimiter limiter2 = server2.GetRequiredService<IDistributedRateLimiterFactory>().Create("auth:1.2.3.4:-", rule);

        Assert.True((await limiter1.AcquireAsync(1, ct)).IsAcquired);
        Assert.True((await limiter2.AcquireAsync(1, ct)).IsAcquired);
        Assert.True((await limiter1.AcquireAsync(1, ct)).IsAcquired);

        using RateLimitLease rejected = await limiter2.AcquireAsync(1, ct); // 4. istek, diğer sunucuda
        Assert.False(rejected.IsAcquired);
        Assert.True(rejected.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter));
        Assert.InRange(retryAfter, TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(1));

        // Başka bölüm (başka IP) etkilenmez.
        RateLimiter other = server1.GetRequiredService<IDistributedRateLimiterFactory>().Create("auth:5.6.7.8:-", rule);
        Assert.True((await other.AcquireAsync(1, ct)).IsAcquired);
    }

    [Fact]
    public async Task Sliding_window_limits_across_servers()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Url), "CAN_REDIS_URL verilmedi.");
        string application = NewApplication();
        CancellationToken ct = TestContext.Current.CancellationToken;
        var rule = new RateLimitRule { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6 };

        await using ServiceProvider server = Server(application, b => b.UseForRateLimiting());
        RateLimiter limiter = server.GetRequiredService<IDistributedRateLimiterFactory>().Create("global:u:1:-", rule);

        int acquired = 0;
        for (int i = 0; i < 8; i++)
        {
            if ((await limiter.AcquireAsync(1, ct)).IsAcquired)
                acquired++;
        }

        Assert.Equal(5, acquired);
    }

    [Fact]
    public async Task Lock_is_exclusive_across_servers_and_expires()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Url), "CAN_REDIS_URL verilmedi.");
        string application = NewApplication();
        CancellationToken ct = TestContext.Current.CancellationToken;

        await using ServiceProvider server1 = Server(application, b => b.UseForDistributedLocks());
        await using ServiceProvider server2 = Server(application, b => b.UseForDistributedLocks());
        IDistributedLock locks1 = server1.GetRequiredService<IDistributedLock>();
        IDistributedLock locks2 = server2.GetRequiredService<IDistributedLock>();

        IAsyncDisposable? held = await locks1.TryAcquireAsync("report", TimeSpan.FromSeconds(30), cancellationToken: ct);
        Assert.NotNull(held);
        Assert.Null(await locks2.TryAcquireAsync("report", TimeSpan.FromSeconds(30), cancellationToken: ct));

        // Diğer sunucu beklerken bırakılırsa alır.
        Task<IAsyncDisposable?> waiting = locks2.TryAcquireAsync("report", TimeSpan.FromSeconds(30), wait: TimeSpan.FromSeconds(5), cancellationToken: ct);
        await Task.Delay(200, ct);
        await held.DisposeAsync();
        IAsyncDisposable? taken = await waiting;
        Assert.NotNull(taken);
        await taken.DisposeAsync();

        // Sahibi bırakmadan çökerse süre sonunda düşer; geç kalan bırakma yeni sahibin kilidine dokunmaz.
        IAsyncDisposable stale = (await locks1.TryAcquireAsync("job", TimeSpan.FromMilliseconds(300), cancellationToken: ct))!;
        await Task.Delay(500, ct);
        IAsyncDisposable fresh = (await locks2.TryAcquireAsync("job", TimeSpan.FromSeconds(30), cancellationToken: ct))!;
        await stale.DisposeAsync();
        Assert.Null(await locks1.TryAcquireAsync("job", TimeSpan.FromSeconds(30), cancellationToken: ct));
        await fresh.DisposeAsync();
    }

    [Fact]
    public async Task Redis_outage_lets_requests_through()
    {
        // Redis'e hiç ulaşılamayan adres: sınırlayıcı isteği engellememeli (siteyi durdurmasın).
        var options = new RedisScaleOutOptions { ConnectionString = "127.0.0.1:1,connectTimeout=200,syncTimeout=200,asyncTimeout=200", ApplicationName = NewApplication() };
        await using var connection = new RedisConnection(options);
        var factory = new RedisRateLimiterFactory(connection, TimeProvider.System, NullLogger<RedisRateLimiterFactory>.Instance);

        RateLimiter limiter = factory.Create("auth:x", new RateLimitRule { PermitLimit = 1, Window = TimeSpan.FromMinutes(1) });

        Assert.True((await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).IsAcquired);
        Assert.True((await limiter.AcquireAsync(1, TestContext.Current.CancellationToken)).IsAcquired);
    }
}
