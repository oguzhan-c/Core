using System.Security.Claims;
using Can.Core.Application;
using Can.Core.Application.Exceptions;
using Microsoft.AspNetCore.Http;

namespace Can.Core.WebApi.CurrentUser;

/// <summary>
/// Aktif tenant'ı doğrulanmış kullanıcının tenant claim'lerinden çözer.
/// <see cref="TenantResolutionMiddleware"/> kullanılıyorsa istek başında bir kez çözülmüş değeri döndürür.
/// </summary>
/// <remarks>
/// Kurallar:
/// <list type="number">
/// <item>Giriş yapılmamışsa tenant yoktur.</item>
/// <item>Header açıksa ve gönderilmişse: değer kullanıcının tenant claim'lerinden biri olmalı (ya da kullanıcı
/// <see cref="CanWebApiOptions.TenantAdminRole"/> rolünde olmalı); değilse erişim reddedilir.</item>
/// <item>Header yoksa ve kullanıcı tek bir tenant'a üyeyse o tenant kullanılır.</item>
/// <item>Birden fazla tenant'a üye olup seçim yapmamışsa tenant yoktur (hiçbir tenant verisi görünmez).</item>
/// </list>
/// </remarks>
public sealed class HttpCurrentTenant : ICurrentTenant
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly CanWebApiOptions _options;

    public HttpCurrentTenant(IHttpContextAccessor httpContextAccessor, CanWebApiOptions options)
    {
        _httpContextAccessor = httpContextAccessor;
        _options = options;
    }

    public object? Id
    {
        get
        {
            HttpContext? context = _httpContextAccessor.HttpContext;
            if (context is null)
                return null;

            if (context.Items.TryGetValue(TenantResolver.ItemKey, out object? resolved) && resolved is ResolvedTenant tenant)
                return tenant.Id;

            // Middleware kullanılmıyorsa: yetkisiz header seçimi sessizce "tenant yok" sayılır (veri görünmez).
            TenantResolution resolution = TenantResolver.Resolve(context, _options);
            return resolution.Forbidden ? null : resolution.TenantId;
        }
    }
}

/// <summary>
/// Tenant'ı istek başında bir kez çözer; kullanıcı header ile üye olmadığı bir tenant seçmeye çalışırsa
/// isteği <see cref="ForbiddenException"/> (403) ile durdurur. <c>UseAuthentication()</c>'dan SONRA eklenmeli.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CanWebApiOptions _options;

    public TenantResolutionMiddleware(RequestDelegate next, CanWebApiOptions options)
    {
        _next = next;
        _options = options;
    }

    public Task InvokeAsync(HttpContext context)
    {
        TenantResolution resolution = TenantResolver.Resolve(context, _options);

        if (resolution.Forbidden)
            throw new ForbiddenException("Seçilen tenant'a erişim yetkin yok.");

        context.Items[TenantResolver.ItemKey] = new ResolvedTenant(resolution.TenantId);
        return _next(context);
    }
}

internal sealed record ResolvedTenant(object? Id);

internal readonly record struct TenantResolution(object? TenantId, bool Forbidden);

internal static class TenantResolver
{
    public const string ItemKey = "Can.Core.CurrentTenant";

    public static TenantResolution Resolve(HttpContext context, CanWebApiOptions options)
    {
        ClaimsPrincipal user = context.User;
        if (user.Identity?.IsAuthenticated != true)
            return new TenantResolution(null, Forbidden: false);

        IReadOnlyCollection<string> memberships = ClaimHelper.FindAll(user, [options.TenantClaimType]);

        string? requested = null;
        if (options.TenantHeaderName is { Length: > 0 } headerName
            && context.Request.Headers.TryGetValue(headerName, out var headerValues))
        {
            requested = headerValues.ToString().Trim();
        }

        if (!string.IsNullOrEmpty(requested))
        {
            bool isMember = memberships.Contains(requested, StringComparer.OrdinalIgnoreCase);
            bool isTenantAdmin =
                options.TenantAdminRole is { Length: > 0 } adminRole
                && ClaimHelper.FindAll(user, options.RoleClaimTypes).Contains(adminRole, StringComparer.OrdinalIgnoreCase);

            if (!isMember && !isTenantAdmin)
                return new TenantResolution(null, Forbidden: true);

            object? tenantId = options.TenantIdParser(requested);
            return tenantId is null ? new TenantResolution(null, Forbidden: true) : new TenantResolution(tenantId, Forbidden: false);
        }

        return memberships.Count == 1
            ? new TenantResolution(options.TenantIdParser(memberships.First()), Forbidden: false)
            : new TenantResolution(null, Forbidden: false);
    }
}
