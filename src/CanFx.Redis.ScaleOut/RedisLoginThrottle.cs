using CanFx.Security.Lockout;
using StackExchange.Redis;

namespace CanFx.Redis.ScaleOut;

/// <summary>
/// Hesap + IP başına hatalı giriş sayaçlarını Redis'te tutar: tüm sunucular aynı sayacı görür. Redis'e ulaşılamazsa
/// sayaç bu sunucunun belleğinde tutulur (sınır kalkmaz, yalnızca sunucular arası paylaşılmaz).
/// </summary>
internal sealed class RedisLoginThrottle(RedisConnection connection, InMemoryLoginThrottle fallback, LoginThrottleOptions options) : ILoginThrottle
{
    // KEYS[1] = sayaç, KEYS[2] = engel; ARGV = en fazla deneme, sayaç ömrü (ms), engel süresi (ms)
    private const string RegisterScript = """
        if redis.call('EXISTS', KEYS[2]) == 1 then return 1 end
        local count = redis.call('INCR', KEYS[1])
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        if count < tonumber(ARGV[1]) then return 0 end
        redis.call('SET', KEYS[2], '1', 'PX', ARGV[3])
        redis.call('DEL', KEYS[1])
        return 1
        """;

    public async Task<bool> IsBlockedAsync(string account, string? ipAddress, CancellationToken cancellationToken = default)
    {
        string key = LoginThrottleKey.Create(account, ipAddress);

        // Kesinti sırasında bu sunucuda oluşan engel, Redis geri gelince de geçerli kalır.
        if (fallback.IsBlocked(key))
            return true;

        try
        {
            IDatabase? database = await connection.TryGetDatabaseAsync().ConfigureAwait(false);
            return database is not null && await database.KeyExistsAsync(BlockKey(key)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException or RedisConnectionException)
        {
            return false;
        }
    }

    public async Task<bool> RegisterFailureAsync(string account, string? ipAddress, LoginThrottleLimits? limits = null, CancellationToken cancellationToken = default)
    {
        string key = LoginThrottleKey.Create(account, ipAddress);
        limits ??= options.ToLimits();

        try
        {
            IDatabase? database = await connection.TryGetDatabaseAsync().ConfigureAwait(false);
            if (database is not null)
            {
                RedisResult blocked = await database
                    .ScriptEvaluateAsync(
                        RegisterScript,
                        [CountKey(key), BlockKey(key)],
                        [limits.MaxAttempts, (long)Math.Ceiling(limits.Window.TotalMilliseconds), (long)Math.Ceiling(limits.LockoutDuration.TotalMilliseconds)])
                    .ConfigureAwait(false);
                return (int)blocked == 1;
            }
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException or RedisConnectionException)
        {
            // aşağıda: bellekteki sayaç
        }

        return fallback.RegisterFailure(key, limits);
    }

    public async Task ResetAsync(string account, string? ipAddress, CancellationToken cancellationToken = default)
    {
        string key = LoginThrottleKey.Create(account, ipAddress);
        fallback.Reset(key);

        try
        {
            IDatabase? database = await connection.TryGetDatabaseAsync().ConfigureAwait(false);
            if (database is not null)
                await database.KeyDeleteAsync([CountKey(key), BlockKey(key)]).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException or RedisConnectionException)
        {
            // Redis yok: bellekteki kayıt silindi; Redis'teki sayaç kendi süresinde düşer.
        }
    }

    // {..} hash etiketi: iki anahtar Redis Cluster'da aynı parçaya düşsün.
    private RedisKey CountKey(string key) => $"{{{connection.Key("login:" + key)}}}:n";

    private RedisKey BlockKey(string key) => $"{{{connection.Key("login:" + key)}}}:b";
}
