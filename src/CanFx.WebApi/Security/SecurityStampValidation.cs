using CanFx.Security;
using CanFx.Security.Events;
using CanFx.Security.Tokens;
using CanFx.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CanFx.WebApi.Security;

/// <summary>
/// Token'daki güvenlik damgası (<c>sstamp</c>) kullanıcının güncel damgasıyla aynı değilse (şifre, e-posta, 2FA
/// değişti) isteği 401 <c>session_revoked</c> ile reddeder ve token cookie'lerini siler: eski oturum token süresi
/// dolmadan kapanır. Damgasız token'lar (eski sürüm) denetlenmez.
/// </summary>
internal sealed class SecurityStampMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.FindFirst(TokenService.SecurityStampClaimType)?.Value is { Length: > 0 } stamp
            && (context.User.FindFirst("sub") ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier))?.Value is { Length: > 0 } userId)
        {
            ISecurityStampValidator validator = context.RequestServices.GetService<ISecurityStampValidator>()
                ?? throw new InvalidOperationException("UseCanSecurityStampValidation için ISecurityStampValidator kayıtlı değil (AddCanSecurityStampValidator<...>()).");

            if (!await validator.IsCurrentAsync(userId, stamp, context.RequestAborted).ConfigureAwait(false))
            {
                await SecurityEventPublishing.PublishAsync(context, new SecurityEvent(SecurityEventTypes.SecurityStampRejected, userId)).ConfigureAwait(false);
                await SessionRejection.WriteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>Oturum geçersiz: 401 <c>session_revoked</c> + token cookie'leri silinir (tarayıcı eski token'ı bırakır).</summary>
internal static class SessionRejection
{
    public static async Task WriteAsync(HttpContext context)
    {
        context.RequestServices.GetService<IAuthCookieService>()?.Clear(context.Response);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status401Unauthorized,
            Title = ProblemTitles.Unauthorized,
            Detail = "Oturum sonlandırıldı; tekrar giriş yap.",
        };
        problem.Extensions["code"] = "session_revoked";
        IProblemDetailsService? problemDetails = context.RequestServices.GetService<IProblemDetailsService>();
        if (problemDetails is null || !await problemDetails.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem }).ConfigureAwait(false))
            await context.Response.WriteAsJsonAsync(problem, context.RequestAborted).ConfigureAwait(false);
    }
}

/// <summary>Gizli değerleri ASP.NET Data Protection ile şifreler.</summary>
internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("CanFx.Security.Secrets.v1");

    public byte[] Protect(byte[] value) => _protector.Protect(value);

    public byte[] Unprotect(byte[] value) => _protector.Unprotect(value);
}

public static class SecurityStampExtensions
{
    /// <summary>
    /// Güvenlik damgası denetimi. <c>UseAuthentication()</c> ve <c>UseCanTenantResolution()</c>'dan SONRA,
    /// <c>UseAuthorization()</c>'dan önce ekle. <c>ISecurityStampValidator</c> kayıtlı olmalı
    /// (<c>AddCanSecurityStampValidator&lt;TContext, TUser, TId&gt;()</c>) ve token'lar damgayla üretilmeli
    /// (<c>TokenSubject.SecurityStamp</c>).
    /// </summary>
    public static IApplicationBuilder UseCanSecurityStampValidation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        StartupValidation.Require<ISecurityStampValidator>(app, "UseCanSecurityStampValidation", "builder.Services.AddCanSecurityStampValidator<TContext, TUser, TId>()");
        return app.UseMiddleware<SecurityStampMiddleware>();
    }

    /// <summary>
    /// <see cref="ISecretProtector"/>'ı ASP.NET Data Protection ile kaydeder (TOTP anahtarlarının şifrelenmesi).
    /// Birden çok sunucuda anahtar halkası paylaşılmalı (Redis ScaleOut ya da <c>PersistKeysTo...</c>); anahtar
    /// halkası kaybolursa şifreli değerler okunamaz.
    /// </summary>
    public static IServiceCollection AddCanSecretProtection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddDataProtection();
        services.TryAddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        return services;
    }
}
