using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CanFx.Redis.ScaleOut;

/// <summary>
/// HTTP çıktı önbelleği Redis'te: sunucuların hepsi aynı önbelleği okur, bir sunucuda yapılan etiket silme hepsinde
/// geçerlidir. Kayıtlar <c>{ApplicationName}:oc:e:{anahtar}</c>, etiketler sıralı küme
/// <c>{ApplicationName}:oc:t:{etiket}</c> (üye: kayıt anahtarı, puan: bitiş zamanı) olarak tutulur. Komutlar tek anahtarlıdır;
/// Redis Cluster'da da çalışır.
/// </summary>
/// <remarks>
/// Redis'e ulaşılamazsa site durmaz: okuma "önbellekte yok" sayılır, yazma atlanır; istekler handler'dan yanıtlanır.
/// Etiket silme hatası yukarı iletilir (çağıran loglar).
/// </remarks>
internal sealed partial class RedisOutputCacheStore : IOutputCacheStore
{
    private readonly RedisConnection _connection;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RedisOutputCacheStore> _logger;

    public RedisOutputCacheStore(RedisConnection connection, TimeProvider timeProvider, ILogger<RedisOutputCacheStore> logger)
    {
        _connection = connection;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        try
        {
            IDatabase db = await _connection.GetDatabaseAsync().ConfigureAwait(false);
            RedisValue value = await db.StringGetAsync(EntryKey(key)).ConfigureAwait(false);
            return value.IsNull ? null : (byte[]?)value;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnavailable(exception);
            return null;
        }
    }

    public async ValueTask SetAsync(string key, byte[] value, string[]? tags, TimeSpan validFor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            await SetCoreAsync(key, value, tags, validFor).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogUnavailable(exception);
        }
    }

    private async Task SetCoreAsync(string key, byte[] value, string[]? tags, TimeSpan validFor)
    {
        IDatabase db = await _connection.GetDatabaseAsync().ConfigureAwait(false);
        string entryKey = EntryKey(key);
        await db.StringSetAsync(entryKey, value, validFor).ConfigureAwait(false);

        if (tags is not { Length: > 0 })
            return;

        long now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        long expiresAt = now + (long)validFor.TotalMilliseconds;
        foreach (string tag in tags.Distinct(StringComparer.Ordinal))
        {
            string tagKey = TagKey(tag);
            await db.SortedSetAddAsync(tagKey, entryKey, expiresAt).ConfigureAwait(false);
            await db.SortedSetRemoveRangeByScoreAsync(tagKey, double.NegativeInfinity, now).ConfigureAwait(false); // süresi dolanlar

            // Etiket kümesi en uzun ömürlü kaydı kadar yaşar
            TimeSpan? ttl = await db.KeyTimeToLiveAsync(tagKey).ConfigureAwait(false);
            if (ttl is null || ttl < validFor)
                await db.KeyExpireAsync(tagKey, validFor).ConfigureAwait(false);
        }
    }

    public async ValueTask EvictByTagAsync(string tag, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tag);
        IDatabase db = await _connection.GetDatabaseAsync().ConfigureAwait(false);
        string tagKey = TagKey(tag);
        RedisValue[] entries = await db.SortedSetRangeByScoreAsync(tagKey).ConfigureAwait(false);

        // Tek anahtarlı silmeler: Cluster'da kayıtlar farklı slot'larda olabilir
        await Task.WhenAll(entries.Select(entry => db.KeyDeleteAsync((string)entry!))).ConfigureAwait(false);
        await db.KeyDeleteAsync(tagKey).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Redis çıktı önbelleğine ulaşılamadı; yanıt önbelleksiz veriliyor.")]
    private partial void LogUnavailable(Exception exception);

    private string EntryKey(string key) => _connection.Key("oc:e:" + key);

    private string TagKey(string tag) => _connection.Key("oc:t:" + tag);
}
