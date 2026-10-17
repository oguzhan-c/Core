using System.Security.Claims;
using Can.Core.Security.Authorization;
using Can.Core.Security.Tokens;
using Can.Core.WebApi.CurrentUser;
using Can.Core.WebApi.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.Security;

/// <summary>
/// Kimliği doğrulanmış kullanıcının rol ve yetki claim'lerini token'dan DEĞİL, <see cref="IUserAuthorizationStore"/>'dan
/// (veritabanı, önbellekli) alır: token'daki rol / yetki claim'leri silinir, güncelleri eklenir. Böylece Admin bir rolü ya
/// da yetkiyi geri aldığında token süresi dolmadan geçerli olur. Kullanıcı bulunamazsa istek 401 <c>session_revoked</c>.
/// </summary>
internal sealed class PermissionResolutionMiddleware(RequestDelegate next, CanWebApiOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity is ClaimsIdentity { IsAuthenticated: true } identity
            && ClaimHelper.FindFirst(context.User, options.UserIdClaimTypes) is { } userId)
        {
            IUserAuthorizationStore store = context.RequestServices.GetRequiredService<IUserAuthorizationStore>();
            UserAuthorizationData? data = await store.GetAsync(userId, context.RequestAborted).ConfigureAwait(false);
            if (data is null)
            {
                await SessionRejection.WriteAsync(context).ConfigureAwait(false);
                return;
            }

            context.User = Rebuild(context.User, identity, data);
        }

        await next(context).ConfigureAwait(false);
    }

    private ClaimsPrincipal Rebuild(ClaimsPrincipal principal, ClaimsIdentity identity, UserAuthorizationData data)
    {
        string roleClaimType = identity.RoleClaimType;
        string permissionClaimType = options.PermissionClaimTypes.FirstOrDefault() ?? TokenService.PermissionClaimType;

        IEnumerable<Claim> kept = identity.Claims.Where(c =>
            c.Type != roleClaimType
            && !options.RoleClaimTypes.Contains(c.Type, StringComparer.Ordinal)
            && !options.PermissionClaimTypes.Contains(c.Type, StringComparer.Ordinal));

        var resolved = new ClaimsIdentity(kept, identity.AuthenticationType, identity.NameClaimType, roleClaimType);
        foreach (string role in data.Roles)
            resolved.AddClaim(new Claim(roleClaimType, role));
        foreach (string permission in data.Permissions)
            resolved.AddClaim(new Claim(permissionClaimType, permission));

        var result = new ClaimsPrincipal(resolved);
        foreach (ClaimsIdentity other in principal.Identities.Where(i => !ReferenceEquals(i, identity)))
            result.AddIdentity(other);
        return result;
    }
}

public static class PermissionResolutionExtensions
{
    /// <summary>
    /// Rolleri ve yetkileri her istekte (önbellekli) sunucudan çözer. <c>UseAuthentication()</c>,
    /// <c>UseCanTenantResolution()</c> ve <c>UseCanSecurityStampValidation()</c>'dan SONRA, <c>UseAuthorization()</c>'dan
    /// önce ekle. <see cref="IUserAuthorizationStore"/> kayıtlı olmalı (<c>AddCanUserAuthorizationStore&lt;TContext, TUser, TId&gt;()</c>).
    /// Token'a yetki yazmak artık gerekmez (cookie küçülür).
    /// </summary>
    public static IApplicationBuilder UseCanPermissionResolution(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        StartupValidation.Require<IUserAuthorizationStore>(app, "UseCanPermissionResolution", "builder.Services.AddCanUserAuthorizationStore<TContext, TUser, TId>()");
        return app.UseMiddleware<PermissionResolutionMiddleware>();
    }
}
