using System.Globalization;
using Can.Core.Security.Tokens;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.WebApi.Security;

/// <summary>
/// <see cref="IAccessTokenDenylist"/>: <c>IDistributedCache</c>'te <c>can:jti:{jti}</c> anahtarı, token'ın süresi (+ saat
/// farkı payı) dolunca silinir. Tek sunucuda bellek içi önbellek yeter; çok sunucuda Redis kayıtlı olmalı
/// (<c>AddCanRedisCache</c>), yoksa bir sunucuda geri alınan token diğerinde geçerli kalır.
/// </summary>
internal sealed class DistributedCacheAccessTokenDenylist(IDistributedCache cache, TimeProvider timeProvider) : IAccessTokenDenylist
{
    /// <summary>Token doğrulamadaki saat farkı toleransından (varsayılan 30 sn) uzun olmalı.</summary>
    private static readonly TimeSpan ClockSkewMargin = TimeSpan.FromMinutes(1);

    private static readonly byte[] Marker = [1];

    public async Task RevokeAsync(string tokenId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        DateTimeOffset until = expiresAt + ClockSkewMargin;
        if (until <= timeProvider.GetUtcNow())
            return; // zaten geçersiz

        await cache.SetAsync(Key(tokenId), Marker, new DistributedCacheEntryOptions { AbsoluteExpiration = until }, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> IsRevokedAsync(string tokenId, CancellationToken cancellationToken = default) =>
        await cache.GetAsync(Key(tokenId), cancellationToken).ConfigureAwait(false) is not null;

    private static string Key(string tokenId) => "can:jti:" + tokenId;
}

/// <summary>
/// Geri alınmış token'la (<c>jti</c>) ya da kapatılmış oturumdan (<c>sid</c>) gelen isteği 401 <c>session_revoked</c> ile
/// reddeder ve cookie'leri siler.
/// </summary>
internal sealed class AccessTokenRevocationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            string? tokenId = context.User.FindFirst("jti")?.Value;
            string? sessionId = context.GetSessionId();
            if (!string.IsNullOrEmpty(tokenId) || !string.IsNullOrEmpty(sessionId))
            {
                IAccessTokenDenylist denylist = context.RequestServices.GetService<IAccessTokenDenylist>()
                    ?? throw new InvalidOperationException("UseCanAccessTokenRevocation için AddCanAccessTokenRevocation() çağrılmalı.");

                if ((!string.IsNullOrEmpty(tokenId) && await denylist.IsRevokedAsync(tokenId, context.RequestAborted).ConfigureAwait(false))
                    || (!string.IsNullOrEmpty(sessionId) && await denylist.IsRevokedAsync(AccessTokenDenylistKeys.Session(sessionId), context.RequestAborted).ConfigureAwait(false)))
                {
                    await SessionRejection.WriteAsync(context).ConfigureAwait(false);
                    return;
                }
            }
        }

        await next(context).ConfigureAwait(false);
    }
}

public static class AccessTokenRevocationExtensions
{
    /// <summary>
    /// Access token geri alma (jti engel listesi). <c>IDistributedCache</c> kayıtlı değilse bellek içi kullanılır.
    /// </summary>
    public static IServiceCollection AddCanAccessTokenRevocation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddDistributedMemoryCache(); // TryAdd: Redis kayıtlıysa o kalır
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAccessTokenDenylist, DistributedCacheAccessTokenDenylist>();
        return services;
    }

    /// <summary>Access token'daki oturum kimliği (<c>sid</c>; <c>TokenSubject.SessionId</c>); yoksa <see langword="null"/>.</summary>
    public static string? GetSessionId(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.User.Identity?.IsAuthenticated == true ? context.User.FindFirst(TokenService.SessionIdClaimType)?.Value : null;
    }

    /// <summary>Geri alınmış token'ları reddeder. <c>UseAuthentication()</c>'dan sonra, <c>UseAuthorization()</c>'dan önce ekle.</summary>
    public static IApplicationBuilder UseCanAccessTokenRevocation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<AccessTokenRevocationMiddleware>();
    }

    /// <summary>
    /// Bu isteğin access token'ını geri alır (çıkışta cookie'leri silmeden önce çağır): token başka bir yere kopyalanmışsa
    /// bile kalan süresinde kullanılamaz. Giriş yapılmamışsa ya da token'da <c>jti</c>/<c>exp</c> yoksa bir şey yapmaz.
    /// </summary>
    public static async Task RevokeCurrentAccessTokenAsync(this HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.User.FindFirst("jti")?.Value is not { Length: > 0 } tokenId
            || !long.TryParse(context.User.FindFirst("exp")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long expires))
            return;

        IAccessTokenDenylist denylist = context.RequestServices.GetService<IAccessTokenDenylist>()
            ?? throw new InvalidOperationException("RevokeCurrentAccessTokenAsync için AddCanAccessTokenRevocation() çağrılmalı.");
        await denylist.RevokeAsync(tokenId, DateTimeOffset.FromUnixTimeSeconds(expires), cancellationToken).ConfigureAwait(false);
    }
}
