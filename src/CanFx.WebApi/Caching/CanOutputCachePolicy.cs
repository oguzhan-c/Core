using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CanFx.Application;
using CanFx.MultiTenancy;
using CanFx.WebApi.Versioning;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace CanFx.WebApi.Caching;

/// <summary>
/// <see cref="CachedResponseAttribute"/> taşıyan uç noktaları önbelleğe alır. ASP.NET'in varsayılan politikası giriş
/// yapmış kullanıcıların isteklerini hiç önbelleğe almaz; bu politika anahtarı kapsamına göre ayırarak alır:
/// <list type="bullet">
/// <item><see cref="ResponseCacheScope.User"/>: tenant + kullanıcı.</item>
/// <item><see cref="ResponseCacheScope.Tenant"/>: tenant + rol/yetki kümesinin özeti (yetkisi farklı iki kullanıcı
/// birbirinin yanıtını alamaz).</item>
/// <item><see cref="ResponseCacheScope.Public"/>: ayrım yok.</item>
/// </list>
/// Her durumda dil, API sürümü, <c>Accept</c> ve tüm sorgu parametreleri anahtardadır.
/// </summary>
internal sealed class CanOutputCachePolicy : IOutputCachePolicy
{
    private const string Anonymous = "-";

    private readonly CanHttpCachingOptions _options;

    public CanOutputCachePolicy(CanHttpCachingOptions options)
    {
        _options = options;
    }

    public ValueTask CacheRequestAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        HttpContext http = context.HttpContext;
        Endpoint? endpoint = http.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<CachedResponseAttribute>() is not { } cache
            || endpoint.Metadata.GetMetadata<NoStoreResponseAttribute>() is not null
            || !(HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)))
        {
            return ValueTask.CompletedTask;
        }

        context.EnableOutputCaching = true;
        context.AllowCacheLookup = true;
        context.AllowCacheStorage = true;
        context.AllowLocking = true; // aynı anahtar için eşzamanlı istekler tek çalıştırmayı bekler (cache stampede)
        context.ResponseExpirationTimeSpan = cache.DurationSeconds > 0 ? TimeSpan.FromSeconds(cache.DurationSeconds) : _options.DefaultDuration;

        CacheVaryByRules vary = context.CacheVaryByRules;
        vary.QueryKeys = cache.VaryByQuery.Length > 0 ? new StringValues(cache.VaryByQuery) : new StringValues("*");
        vary.HeaderNames = new StringValues(new[] { HeaderNames.Accept }.Concat(cache.VaryByHeader).ToArray());
        vary.VaryByValues["can:culture"] = CultureInfo.CurrentUICulture.Name;
        if (http.GetApiVersion() is { } version)
            vary.VaryByValues["can:api-version"] = version.Major.ToString(CultureInfo.InvariantCulture);

        if (cache.Scope != ResponseCacheScope.Public)
        {
            vary.VaryByValues["can:tenant"] = http.RequestServices.GetService<TenantContext>()?.TenantId ?? Anonymous;
            ICurrentUser? user = http.RequestServices.GetService<ICurrentUser>();
            vary.VaryByValues["can:principal"] = cache.Scope == ResponseCacheScope.User
                ? "u:" + (user?.Id ?? Anonymous)
                : "r:" + AuthorizationFingerprint(user);
        }

        foreach (string tag in cache.Tags)
            context.Tags.Add(tag);

        return ValueTask.CompletedTask;
    }

    public ValueTask ServeFromCacheAsync(OutputCacheContext context, CancellationToken cancellation) => ValueTask.CompletedTask;

    public ValueTask ServeResponseAsync(OutputCacheContext context, CancellationToken cancellation)
    {
        if (!context.EnableOutputCaching)
            return ValueTask.CompletedTask;

        HttpResponse response = context.HttpContext.Response;
        // Yalnızca başarılı yanıt; cookie yazan yanıt (ör. token yenileme) başkasına asla dönmemeli.
        if (response.StatusCode != StatusCodes.Status200OK
            || !StringValues.IsNullOrEmpty(response.Headers.SetCookie)
            || response.Headers.CacheControl.Any(v => v is not null && v.Contains("no-store", StringComparison.OrdinalIgnoreCase)))
        {
            context.AllowCacheStorage = false;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Giriş durumu + sıralı roller ve yetkiler: aynı yetkiye sahip kullanıcılar aynı anahtarı paylaşır.</summary>
    private static string AuthorizationFingerprint(ICurrentUser? user)
    {
        if (user is not { IsAuthenticated: true })
            return Anonymous;

        var builder = new StringBuilder("auth");
        foreach (string role in user.Roles.Order(StringComparer.OrdinalIgnoreCase))
            builder.Append("\nr:").Append(role.ToUpperInvariant());
        foreach (string permission in user.Permissions.Order(StringComparer.OrdinalIgnoreCase))
            builder.Append("\np:").Append(permission.ToUpperInvariant());

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
