using Can.Core.Application;
using StackExchange.Redis;

namespace Can.Core.Redis.ScaleOut;

/// <summary>
/// Redis ile dağıtık kilit: <c>SET anahtar jeton NX PX süre</c>. Bırakırken yalnızca jeton hâlâ bizimse silinir (süresi
/// dolup başkası aldıysa onun kilidine dokunulmaz). Tek Redis sunucusu için doğrudur; Redis'in kendisi çökerse kilit
/// garantisi yoktur (Redlock gerekmez: işler zaten tekrar çalıştırılabilir yazılmalı).
/// </summary>
internal sealed class RedisDistributedLock(RedisConnection connection, TimeProvider timeProvider) : IDistributedLock
{
    private const string AcquireScript = "return redis.call('SET', KEYS[1], ARGV[1], 'NX', 'PX', ARGV[2]) and 1 or 0";

    private const string ReleaseScript = """
        if redis.call('GET', KEYS[1]) == ARGV[1] then return redis.call('DEL', KEYS[1]) end
        return 0
        """;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    public async Task<IAsyncDisposable?> TryAcquireAsync(string resource, TimeSpan expiry, TimeSpan wait = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiry, TimeSpan.Zero);

        RedisKey key = connection.Key($"lock:{resource}");
        string token = Guid.NewGuid().ToString("N");
        IDatabase database = await connection.GetDatabaseAsync().ConfigureAwait(false);
        DateTimeOffset deadline = timeProvider.GetUtcNow() + wait;

        while (true)
        {
            RedisResult acquired = await database
                .ScriptEvaluateAsync(AcquireScript, [key], [token, (long)Math.Ceiling(expiry.TotalMilliseconds)])
                .ConfigureAwait(false);
            if ((int)acquired == 1)
                return new Handle(database, key, token);

            if (timeProvider.GetUtcNow() >= deadline)
                return null;

            await Task.Delay(PollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Handle(IDatabase database, RedisKey key, string token) : IAsyncDisposable
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                await database.ScriptEvaluateAsync(ReleaseScript, [key], [token]).ConfigureAwait(false);
        }
    }
}
