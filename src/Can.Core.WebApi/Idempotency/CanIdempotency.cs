using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.WebApi.Idempotency;

/// <summary><c>AddCanIdempotency(...)</c> ayarları.</summary>
public sealed class CanIdempotencyOptions
{
    /// <summary>İstemcinin anahtarı gönderdiği başlık (IETF taslağı: <c>Idempotency-Key</c>).</summary>
    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>Saklanan yanıtın ömrü: istemci bu süre içinde aynı anahtarla tekrar denerse aynı yanıtı alır.</summary>
    public TimeSpan Expiration { get; set; } = TimeSpan.FromHours(24);

    /// <summary>İşlenmekte olan isteğin kilidinin en uzun ömrü (sunucu çökerse kilit bu süre sonunda düşer).</summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromMinutes(1);

    public int MaxKeyLength { get; set; } = 255;

    /// <summary>Bundan büyük yanıtlar saklanmaz (istek yine işlenir; tekrar denemede yeniden çalışır).</summary>
    public int MaxResponseBodySize { get; set; } = 1024 * 1024;

    /// <summary>Tekrarda geri verilen yanıt başlıkları (Content-Type her zaman). Çerezler asla saklanmaz.</summary>
    public IList<string> ReplayedHeaders { get; } = ["Location", "ETag", "Last-Modified", "Content-Location"];
}

/// <summary>
/// Endpoint'i idempotent yapar (controller'larda öznitelik, minimal API'de <c>.WithIdempotency()</c>).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class IdempotentAttribute : Attribute
{
    /// <summary>Anahtar zorunlu mu (yoksa 400). Kapalıysa anahtarsız istekler olduğu gibi işlenir.</summary>
    public bool Required { get; init; } = true;
}

public static class CanIdempotencyExtensions
{
    /// <summary>
    /// Idempotency anahtarı desteği. Yanıtlar <c>IDistributedCache</c>'te saklanır (kayıtlı değilse bellek içi; birden
    /// çok sunucuda Redis gerekir: <c>AddCanRedisCache</c>), eşzamanlı aynı anahtar <c>IDistributedLock</c> ile önlenir.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanIdempotency();
    /// app.UseAuthentication(); app.UseCanTenantResolution(); app.UseAuthorization();
    /// app.UseCanIdempotency();
    /// app.MapPost("/orders", CreateOrder).WithIdempotency();
    /// </code>
    /// </example>
    public static IServiceCollection AddCanIdempotency(this IServiceCollection services, Action<CanIdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = new CanIdempotencyOptions();
        configure?.Invoke(options);
        services.RemoveAll<CanIdempotencyOptions>();
        services.AddSingleton(options);
        services.AddDistributedMemoryCache(); // TryAdd: Redis kayıtlıysa o kalır
        services.TryAddSingleton<LocalIdempotencyLocks>();
        return services;
    }

    /// <summary>
    /// <c>UseAuthentication</c>, <c>UseCanTenantResolution</c> ve <c>UseAuthorization</c>'dan SONRA ekle: anahtar
    /// kullanıcı ve tenant'a göre ayrılır, yetkisiz istekler saklanmaz.
    /// </summary>
    public static IApplicationBuilder UseCanIdempotency(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<IdempotencyMiddleware>();
    }

    /// <summary>Endpoint'i (ya da grubu) idempotent yapar.</summary>
    /// <param name="builder">Endpoint ya da route grubu.</param>
    /// <param name="required">Anahtar zorunlu mu.</param>
    public static TBuilder WithIdempotency<TBuilder>(this TBuilder builder, bool required = true)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new IdempotentAttribute { Required = required });
    }
}
