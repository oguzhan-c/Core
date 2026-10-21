using System.IO.Compression;
using CanFx.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CanFx.WebApi.Caching;

/// <summary>Önbelleğe alınan yanıtın kimlerle paylaşıldığı.</summary>
public enum ResponseCacheScope
{
    /// <summary>
    /// Kullanıcıya özel (varsayılan, en güvenlisi): anahtar tenant + kullanıcı. Kullanıcının kendi verisi (<c>/me</c>,
    /// "siparişlerim") için.
    /// </summary>
    User = 0,

    /// <summary>
    /// Tenant içinde aynı rol ve yetkilere sahip kullanıcılar paylaşır: anahtar tenant + rol/yetki kümesi. Yanıt
    /// kullanıcıya göre değişmiyorsa (ürün kataloğu, kategori listesi) çok daha fazla isabet sağlar.
    /// </summary>
    Tenant = 1,

    /// <summary>
    /// Herkes aynı yanıtı alır (giriş yapmamışlar dahil); anahtarda kullanıcı ve tenant yoktur. YALNIZCA herkese açık
    /// veriler için (ülke listesi, herkese açık duyurular).
    /// </summary>
    Public = 2,
}

/// <summary>
/// Yanıt sunucuda önbelleğe alınır (controller'larda öznitelik, minimal API'de <c>.WithResponseCache(...)</c>).
/// Yalnızca GET/HEAD ve 200 yanıtları saklanır; <c>Set-Cookie</c> içeren yanıtlar asla saklanmaz.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class CachedResponseAttribute : Attribute
{
    /// <param name="scope">Yanıtın kimlerle paylaşıldığı.</param>
    public CachedResponseAttribute(ResponseCacheScope scope = ResponseCacheScope.User)
    {
        Scope = scope;
    }

    public ResponseCacheScope Scope { get; }

    /// <summary>Saniye cinsinden ömür; 0 ise <see cref="CanHttpCachingOptions.DefaultDuration"/>.</summary>
    public int DurationSeconds { get; init; }

    /// <summary>
    /// Silme etiketleri (<c>"products"</c>, <c>CacheTags.Entity&lt;Product&gt;()</c>). Aynı etiket
    /// <c>ICacheRemoverRequest.CacheTagsToRemove</c> ile ya da <c>AddCanEntityCacheInvalidation()</c> ile silinince
    /// bu yanıtlar da düşer.
    /// </summary>
    public string[] Tags { get; init; } = [];

    /// <summary>Anahtara giren sorgu parametreleri; boşsa hepsi (varsayılan).</summary>
    public string[] VaryByQuery { get; init; } = [];

    /// <summary>Anahtara giren ek istek başlıkları (<c>Accept</c>, dil ve API sürümü zaten dahildir).</summary>
    public string[] VaryByHeader { get; init; } = [];
}

/// <summary>
/// Yanıt hiçbir yerde saklanmaz: <c>Cache-Control: no-store</c>, ETag üretilmez, sunucu önbelleği kullanılmaz.
/// Hassas veriler için (kimlik bilgisi, ödeme, sağlık verisi, kurtarma kodları).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class NoStoreResponseAttribute : Attribute;

/// <summary><c>AddCanHttpCaching(...)</c> ayarları.</summary>
public sealed class CanHttpCachingOptions
{
    /// <summary>Sunucu tarafı yanıt önbelleği (<c>WithResponseCache</c> işaretli uç noktalar).</summary>
    public bool UseOutputCache { get; set; } = true;

    /// <summary>Uç nokta süre vermediğinde yanıtın ömrü.</summary>
    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Bundan büyük yanıtlar sunucu önbelleğine alınmaz.</summary>
    public long MaximumCachedBodySize { get; set; } = 1024 * 1024;

    /// <summary>Bellek içi önbelleğin toplam boyut sınırı (Redis kullanılıyorsa geçersiz).</summary>
    public long OutputCacheSizeLimit { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    /// GET yanıtlarına ETag eklenir; istemci <c>If-None-Match</c> ile aynı değeri gönderirse gövdesiz 304 döner
    /// (veri değişmediyse ağdan tekrar inmez).
    /// </summary>
    public bool UseETags { get; set; } = true;

    /// <summary>ETag için yanıt bu boyuta kadar bellekte tutulur; büyükse ETag'siz, akış olarak gönderilir.</summary>
    public int MaximumETagBodySize { get; set; } = 1024 * 1024;

    /// <summary>Yanıtlar Brotli ya da gzip ile sıkıştırılır (istemcinin <c>Accept-Encoding</c>'ine göre).</summary>
    /// <remarks>Reverse proxy (nginx, Azure Front Door, CloudFront) zaten sıkıştırıyorsa kapat; iki kez yapılmasın.</remarks>
    public bool UseCompression { get; set; } = true;

    /// <summary>
    /// Sıkıştırma düzeyi. Dinamik yanıtlar için <see cref="CompressionLevel.Fastest"/> önerilir: <c>Optimal</c> biraz
    /// daha küçük çıktı için çok daha fazla CPU harcar.
    /// </summary>
    public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Fastest;

    /// <summary>
    /// HTTPS yanıtları da sıkıştırılır. ASP.NET varsayılanı kapalıdır (BREACH saldırısı: gizli bir değer ile saldırganın
    /// kontrol ettiği metin aynı sıkıştırılmış gövdedeyse). Token'lar gövdede değil, cookie'dedir; gövdesinde gizli değer
    /// taşıyan uç noktalara <c>.WithNoStore()</c> ya da bu seçeneği kapat.
    /// </summary>
    public bool CompressHttps { get; set; } = true;

    /// <summary>Varsayılanlara (<c>text/*</c>, <c>application/json</c>...) ek olarak sıkıştırılan içerik türleri.</summary>
    public IList<string> AdditionalCompressedMimeTypes { get; } = ["application/problem+json"];

    /// <summary>
    /// <c>Cache-Control</c> verilmemiş GET 200 yanıtlarına güvenli varsayılan eklenir: giriş yapmış kullanıcıya
    /// <c>private, no-cache</c> (paylaşılan proxy saklamaz, tarayıcı her seferinde ETag ile doğrular), diğerlerine
    /// <c>no-cache</c>.
    /// </summary>
    public bool SetDefaultCacheControl { get; set; } = true;
}

public static class HttpCachingExtensions
{
    /// <summary>
    /// HTTP performansı: sunucu tarafı yanıt önbelleği (etiketle silinebilir, kullanıcı/tenant/rol ayrımlı), ETag ve
    /// 304, Brotli/gzip sıkıştırma ve güvenli <c>Cache-Control</c> varsayılanları. Önbellek bellektedir; birden çok
    /// sunucuda <c>AddCanRedisScaleOut(...).UseForOutputCache()</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanHttpCaching();
    ///
    /// app.UseAuthentication(); app.UseCanTenantResolution(); app.UseAuthorization();
    /// app.UseCanHttpCaching();
    ///
    /// app.MapGet("/products", ListProducts).WithResponseCache(ResponseCacheScope.Tenant, CacheTags.Entity&lt;Product&gt;());
    /// app.MapGet("/me", GetProfile).WithResponseCache(); // kullanıcıya özel
    /// app.MapGet("/me/recovery-codes", GetCodes).WithNoStore();
    /// </code>
    /// </example>
    public static IServiceCollection AddCanHttpCaching(this IServiceCollection services, Action<CanHttpCachingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new CanHttpCachingOptions();
        configure?.Invoke(options);
        if (options.DefaultDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("CanHttpCachingOptions.DefaultDuration sıfırdan büyük olmalı.");

        services.RemoveAll<CanHttpCachingOptions>();
        services.AddSingleton(options);

        if (options.UseOutputCache)
        {
            services.AddOutputCache(o =>
            {
                o.SizeLimit = options.OutputCacheSizeLimit;
                o.MaximumBodySize = options.MaximumCachedBodySize;
                o.DefaultExpirationTimeSpan = options.DefaultDuration;
                o.AddBasePolicy(new CanOutputCachePolicy(options));
            });
            services.TryAddEnumerable(ServiceDescriptor.Singleton<ICacheInvalidationTarget, OutputCacheInvalidationTarget>());
        }

        if (options.UseCompression)
        {
            services.AddResponseCompression(o =>
            {
                o.EnableForHttps = options.CompressHttps;
                o.Providers.Add<BrotliCompressionProvider>();
                o.Providers.Add<GzipCompressionProvider>();
                o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(options.AdditionalCompressedMimeTypes).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            });
            services.Configure<BrotliCompressionProviderOptions>(o => o.Level = options.CompressionLevel);
            services.Configure<GzipCompressionProviderOptions>(o => o.Level = options.CompressionLevel);
        }

        return services;
    }

    /// <summary>
    /// <c>UseAuthentication</c>, <c>UseCanTenantResolution</c> ve <c>UseAuthorization</c>'dan SONRA ekle: önbellek
    /// anahtarı kullanıcı ve tenant'a göre ayrılır, yetkisiz istek önbellekten yanıt alamaz.
    /// </summary>
    public static IApplicationBuilder UseCanHttpCaching(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        CanHttpCachingOptions options = app.ApplicationServices.GetService<CanHttpCachingOptions>()
            ?? throw new InvalidOperationException("UseCanHttpCaching için önce builder.Services.AddCanHttpCaching() çağır.");

        if (options.UseCompression)
            app.UseResponseCompression();
        if (options.UseOutputCache)
            app.UseOutputCache();
        if (options.UseETags || options.SetDefaultCacheControl)
            app.UseMiddleware<HttpCacheHeadersMiddleware>();
        return app;
    }

    /// <summary>Yanıtı sunucuda önbelleğe alır (varsayılan süre).</summary>
    /// <param name="builder">Uç nokta ya da route grubu.</param>
    /// <param name="scope">Yanıtın kimlerle paylaşıldığı.</param>
    /// <param name="tags">Silme etiketleri.</param>
    public static TBuilder WithResponseCache<TBuilder>(this TBuilder builder, ResponseCacheScope scope = ResponseCacheScope.User, params string[] tags)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new CachedResponseAttribute(scope) { Tags = tags });
    }

    /// <summary>Yanıtı verilen süre boyunca sunucuda önbelleğe alır.</summary>
    /// <param name="builder">Uç nokta ya da route grubu.</param>
    /// <param name="scope">Yanıtın kimlerle paylaşıldığı.</param>
    /// <param name="duration">Ömür.</param>
    /// <param name="tags">Silme etiketleri.</param>
    public static TBuilder WithResponseCache<TBuilder>(this TBuilder builder, ResponseCacheScope scope, TimeSpan duration, params string[] tags)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        return builder.WithMetadata(new CachedResponseAttribute(scope) { DurationSeconds = (int)Math.Ceiling(duration.TotalSeconds), Tags = tags });
    }

    /// <summary>Tüm ayarlarla (sorgu/başlık ayrımı).</summary>
    /// <param name="builder">Uç nokta ya da route grubu.</param>
    /// <param name="attribute">Ayarlar.</param>
    public static TBuilder WithResponseCache<TBuilder>(this TBuilder builder, CachedResponseAttribute attribute)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(attribute);
        return builder.WithMetadata(attribute);
    }

    /// <summary>Yanıt hiçbir yerde saklanmaz (<c>Cache-Control: no-store</c>; ETag ve sunucu önbelleği yok).</summary>
    /// <param name="builder">Uç nokta ya da route grubu.</param>
    public static TBuilder WithNoStore<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new NoStoreResponseAttribute());
    }
}

/// <summary>Etiket silmelerini HTTP çıktı önbelleğine iletir.</summary>
internal sealed partial class OutputCacheInvalidationTarget : ICacheInvalidationTarget
{
    private readonly IOutputCacheStore _store;
    private readonly ILogger<OutputCacheInvalidationTarget> _logger;

    public OutputCacheInvalidationTarget(IOutputCacheStore store, ILogger<OutputCacheInvalidationTarget> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async ValueTask InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        foreach (string tag in tags)
        {
            try
            {
                await _store.EvictByTagAsync(tag, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Veri kaydedildi; önbellek silinemedi diye işlem hata dönmesin. Kayıt süresi dolunca düşer.
                LogFailed(exception, tag);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "HTTP önbellek etiketi silinemedi: {Tag}")]
    private partial void LogFailed(Exception exception, string tag);
}
