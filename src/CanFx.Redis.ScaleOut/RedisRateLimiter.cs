using System.Globalization;
using System.Threading.RateLimiting;
using CanFx.WebApi.RateLimiting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace CanFx.Redis.ScaleOut;

/// <summary>Hız sınırı sayaçlarını Redis'te tutar: tüm sunucular aynı sınırı paylaşır.</summary>
internal sealed partial class RedisRateLimiterFactory(RedisConnection connection, TimeProvider timeProvider, ILogger<RedisRateLimiterFactory> logger)
    : IDistributedRateLimiterFactory
{
    public RateLimiter Create(string partitionKey, RateLimitRule rule) =>
        new RedisRateLimiter(connection, connection.Key($"ratelimit:{partitionKey}"), rule, timeProvider, this);

    private int _unavailable;

    /// <summary>Redis'e ulaşılamadı: sınır bu sunucunun belleğinde uygulanır. Durum değişince bir kez loglanır.</summary>
    internal void Unavailable(Exception? exception)
    {
        if (Interlocked.Exchange(ref _unavailable, 1) == 0)
            LogUnavailable(logger, exception);
    }

    internal void Available()
    {
        if (Interlocked.Exchange(ref _unavailable, 0) == 1)
            LogRecovered(logger);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hız sınırı için Redis'e ulaşılamıyor; sınır her sunucunun kendi belleğinde uygulanıyor (sunucular arası paylaşılmıyor).")]
    private static partial void LogUnavailable(ILogger logger, Exception? exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hız sınırı yeniden Redis üzerinden uygulanıyor.")]
    private static partial void LogRecovered(ILogger logger);
}

internal sealed class RedisRateLimiter : RateLimiter
{
    // KEYS[1] = bu pencere, KEYS[2] = önceki pencere; ARGV = limit, ömür (ms), önceki pencere ağırlığı, istenen izin
    private const string Script = """
        local current = tonumber(redis.call('GET', KEYS[1]) or '0')
        local previous = 0
        if ARGV[3] ~= '0' then previous = tonumber(redis.call('GET', KEYS[2]) or '0') end
        if math.floor(previous * tonumber(ARGV[3])) + current + tonumber(ARGV[4]) > tonumber(ARGV[1]) then return 0 end
        redis.call('INCRBY', KEYS[1], ARGV[4])
        redis.call('PEXPIRE', KEYS[1], ARGV[2])
        return 1
        """;

    private static readonly RateLimitLease Unattempted = new Lease(false, null);

    private readonly RedisConnection _connection;
    private readonly string _key;
    private readonly RateLimitRule _rule;
    private readonly TimeProvider _timeProvider;
    private readonly RedisRateLimiterFactory _factory;
    private readonly Lock _localGate = new();
    private long _lastUsedTicks;
    private long _localWindow = -1;
    private int _localCount;

    public RedisRateLimiter(RedisConnection connection, string key, RateLimitRule rule, TimeProvider timeProvider, RedisRateLimiterFactory factory)
    {
        if (rule.PermitLimit <= 0 || rule.Window <= TimeSpan.Zero)
            throw new ArgumentException("Sınır ve pencere pozitif olmalı.", nameof(rule));

        _connection = connection;
        _key = key;
        _rule = rule;
        _timeProvider = timeProvider;
        _factory = factory;
        _lastUsedTicks = timeProvider.GetTimestamp();
    }

    /// <summary>Kullanılmayan bölümlerin bellekten atılabilmesi için (Redis'teki sayaç zaten süreyle silinir).</summary>
    public override TimeSpan? IdleDuration => _timeProvider.GetElapsedTime(Interlocked.Read(ref _lastUsedTicks));

    public override RateLimiterStatistics? GetStatistics() => null;

    /// <summary>
    /// Senkron deneme ağ çağrısı yapmaz: alınamadı döner, ASP.NET Core'un sınırlayıcı ara katmanı bu durumda asenkron
    /// <see cref="AcquireAsyncCore"/>'u çağırır (thread bloklanmaz).
    /// </summary>
    protected override RateLimitLease AttemptAcquireCore(int permitCount) => Unattempted;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _lastUsedTicks, _timeProvider.GetTimestamp());
        if (permitCount == 0)
            return new Lease(true, null);
        if (permitCount > _rule.PermitLimit)
            return new Lease(false, _rule.Window);

        long windowMs = (long)_rule.Window.TotalMilliseconds;
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        long index = nowMs / windowMs;
        long elapsedMs = nowMs - (index * windowMs);
        double previousWeight = _rule.SegmentsPerWindow > 1 ? 1 - (elapsedMs / (double)windowMs) : 0;

        // {..} hash etiketi: iki anahtar Redis Cluster'da aynı parçaya düşsün.
        RedisKey[] keys = [$"{{{_key}}}:{index}", $"{{{_key}}}:{index - 1}"];
        RedisValue[] values =
        [
            _rule.PermitLimit,
            windowMs * 2, // önceki pencere bir sonraki pencerede de okunur
            previousWeight.ToString("0.####", CultureInfo.InvariantCulture),
            permitCount,
        ];

        var retryAfter = TimeSpan.FromMilliseconds(windowMs - elapsedMs);

        // Bağlantı yokken komut zaman aşımına kadar bekler: beklemeden bu sunucunun sayacına geç.
        IDatabase? database = await _connection.TryGetDatabaseAsync().ConfigureAwait(false);
        if (database is null)
        {
            _factory.Unavailable(null);
            return AcquireLocally(permitCount, index, retryAfter);
        }

        try
        {
            RedisResult result = await database.ScriptEvaluateAsync(Script, keys, values).ConfigureAwait(false);
            _factory.Available();
            return (int)result == 1 ? new Lease(true, null) : new Lease(false, retryAfter);
        }
        catch (Exception ex) when (ex is RedisException or RedisTimeoutException or RedisConnectionException)
        {
            _factory.Unavailable(ex);
            return AcquireLocally(permitCount, index, retryAfter);
        }
    }

    /// <summary>
    /// Redis yokken yedek: aynı sınır, sabit pencereyle, yalnızca bu sunucunun belleğinde. Sunucular birbirini görmez
    /// (N sunucuda toplam sınır en çok N katına çıkar) ama sınır hiç kalkmaz.
    /// </summary>
    private RateLimitLease AcquireLocally(int permitCount, long windowIndex, TimeSpan retryAfter)
    {
        lock (_localGate)
        {
            if (_localWindow != windowIndex)
            {
                _localWindow = windowIndex;
                _localCount = 0;
            }

            if (_localCount + permitCount > _rule.PermitLimit)
                return new Lease(false, retryAfter);

            _localCount += permitCount;
            return new Lease(true, null);
        }
    }

    private sealed class Lease(bool acquired, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames => retryAfter is null ? [] : [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (retryAfter is { } value && metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = value;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
