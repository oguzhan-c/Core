using CanFx.Domain.Results;
using CanFx.Mediator;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace CanFx.Application.Behaviors;

/// <summary>
/// <see cref="ICachableRequest"/> yanıtlarını <see cref="HybridCache"/> ile önbelleğe alır
/// (bellek + varsa Redis gibi dağıtık önbellek; aynı anahtar için eşzamanlı istekler tek çağrıya indirgenir).
/// </summary>
/// <remarks>
/// Aktif tenant varsa anahtarın başına otomatik eklenir (<c>tenant:{id}:{CacheKey}</c>); böylece bir tenant'ın
/// önbelleğe alınmış verisi başka bir tenant'a dönmez. Kullanıcıya özel sorgularda (ör. "/me") kullanıcı Id'sini
/// <see cref="ICachableRequest.CacheKey"/>'e kendin eklemelisin.
/// </remarks>
public sealed class CachingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICachableRequest
{
    private readonly HybridCache _cache;
    private readonly ICurrentTenant _currentTenant;
    private readonly CanApplicationOptions _options;
    private readonly ILogger<CachingBehavior<TRequest, TResponse>> _logger;

    public CachingBehavior(
        HybridCache cache,
        ICurrentTenant currentTenant,
        CanApplicationOptions options,
        ILogger<CachingBehavior<TRequest, TResponse>> logger)
    {
        _cache = cache;
        _currentTenant = currentTenant;
        _options = options;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (request.BypassCache)
            return await next().ConfigureAwait(false);

        var entryOptions = new HybridCacheEntryOptions
        {
            Expiration = request.CacheExpiration ?? _options.DefaultCacheExpiration,
        };

        bool fromHandler = false;
        string cacheKey = _currentTenant.Id is { } tenantId ? $"tenant:{tenantId}:{request.CacheKey}" : request.CacheKey;

        TResponse response;
        try
        {
            response = await _cache
                .GetOrCreateAsync(
                    cacheKey,
                    async _ =>
                    {
                        fromHandler = true;
                        TResponse fresh = await next().ConfigureAwait(false);

                        // Başarısız Result önbelleğe yazılmaz (ör. geçici "bulunamadı").
                        if (fresh is IResultBase { IsSuccess: false })
                            throw new FailedResultSignal(fresh);

                        return fresh;
                    },
                    entryOptions,
                    request.CacheTags,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (FailedResultSignal signal)
        {
            return (TResponse)signal.Response!;
        }

        if (!fromHandler)
            _logger.LogDebug("Önbellekten geldi: {CacheKey}", cacheKey);

        return response;
    }
}

/// <summary>
/// <see cref="ICacheRemoverRequest"/> başarıyla tamamlandıktan sonra (transaction commit edildikten sonra)
/// ilgili önbellek etiketlerini ve anahtarlarını siler. Handler hata verirse hiçbir şey silinmez.
/// Etiketler kayıtlı tüm <see cref="ICacheInvalidationTarget"/>'lara iletilir; anahtarlar yalnızca HybridCache'tedir.
/// </summary>
public sealed class CacheRemovingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : ICacheRemoverRequest
{
    private readonly HybridCache _cache;
    private readonly IEnumerable<ICacheInvalidationTarget> _targets;
    private readonly ICurrentTenant _currentTenant;
    private readonly ILogger<CacheRemovingBehavior<TRequest, TResponse>> _logger;

    public CacheRemovingBehavior(
        HybridCache cache,
        IEnumerable<ICacheInvalidationTarget> targets,
        ICurrentTenant currentTenant,
        ILogger<CacheRemovingBehavior<TRequest, TResponse>> logger)
    {
        _cache = cache;
        _targets = targets;
        _currentTenant = currentTenant;
        _logger = logger;
    }

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        TResponse response = await next().ConfigureAwait(false);

        // İşlem başarısızsa veri değişmedi; önbellek olduğu gibi kalır.
        if (response is IResultBase { IsSuccess: false })
            return response;

        // Etiketler tüm hedeflere gider: HybridCache ve (WebApi'de AddCanHttpCaching açıksa) HTTP çıktı önbelleği.
        IReadOnlyCollection<string> tags = request.CacheTagsToRemove;
        if (tags.Count > 0)
        {
            await _targets.InvalidateTagsAsync(tags, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Önbellek etiketleri silindi: {CacheTags}", tags);
        }

        foreach (string requestKey in request.CacheKeysToRemove)
        {
            // CachingBehavior ile aynı anahtar biçimi
            string key = _currentTenant.Id is { } tenantId ? $"tenant:{tenantId}:{requestKey}" : requestKey;
            await _cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Önbellek anahtarı silindi: {CacheKey}", key);
        }

        return response;
    }
}

/// <summary>Etiket silmelerini <see cref="HybridCache"/>'e iletir (<c>AddCanApplication</c> kaydeder).</summary>
public sealed partial class HybridCacheInvalidationTarget : ICacheInvalidationTarget
{
    private readonly HybridCache _cache;
    private readonly ILogger<HybridCacheInvalidationTarget> _logger;

    public HybridCacheInvalidationTarget(HybridCache cache, ILogger<HybridCacheInvalidationTarget> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public async ValueTask InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        try
        {
            await _cache.RemoveByTagAsync(tags, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Veri kaydedildi; önbellek silinemedi diye işlem hata dönmesin. Kayıtlar süresi dolunca düşer.
            LogFailed(exception, string.Join(", ", tags));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "HybridCache etiketleri silinemedi: {Tags}")]
    private partial void LogFailed(Exception exception, string tags);
}
