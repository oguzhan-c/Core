using Can.Core.Application.Exceptions;
using Can.Core.MultiTenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.WebApi.CurrentUser;

/// <summary>
/// İstek başında aktif tenant'ı doğrulanmış token'dan çözer ve <see cref="TenantContext"/>'e yazar.
/// <c>UseAuthentication()</c>'dan SONRA eklenmeli.
/// </summary>
/// <remarks>
/// Kurallar:
/// <list type="number">
/// <item>Tenant YALNIZCA imzalı token'daki <see cref="CanWebApiOptions.TenantClaimType"/> claim'inden gelir.
/// Header, query string ya da istek gövdesi hiçbir zaman okunmaz; istemci tenant'ı değiştiremez.</item>
/// <item>Anonim isteklerde ya da token'da tek bir tenant yoksa tenant yoktur (tenant'a ait veri görünmez).</item>
/// <item><see cref="ITenantStore"/> kayıtlıysa tenant'ın tanımı yüklenir (tenant başına veritabanı için gerekli);
/// kayıtlı olmayan ya da pasif tenant'a erişim <see cref="ForbiddenException"/> (403) ile reddedilir. Böylece
/// pasife alınan bir tenant'ın henüz süresi dolmamış token'ları da hemen geçersiz olur.</item>
/// </list>
/// Tenant değiştirmek isteyen kullanıcı için sunucu üyeliği kontrol eder ve o tenant'a ait yeni bir token üretir.
/// </remarks>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly CanWebApiOptions _options;

    public TenantResolutionMiddleware(RequestDelegate next, CanWebApiOptions options)
    {
        _next = next;
        _options = options;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        TenantContext tenantContext = context.RequestServices.GetRequiredService<TenantContext>();
        string? tenantId = ResolveTenantId(context);

        if (tenantId is null)
        {
            tenantContext.Clear();
        }
        else if (context.RequestServices.GetService<ITenantStore>() is { } store)
        {
            TenantInfo? tenant = await store.FindAsync(tenantId, context.RequestAborted).ConfigureAwait(false);

            if (tenant is null || !string.Equals(tenant.Id, tenantId, StringComparison.OrdinalIgnoreCase))
                throw new ForbiddenException("Tenant bulunamadı.");

            if (!tenant.IsActive)
                throw new ForbiddenException("Tenant pasif.");

            tenantContext.Set(tenant);
        }
        else
        {
            tenantContext.Set(tenantId);
        }

        await _next(context).ConfigureAwait(false);
    }

    private string? ResolveTenantId(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
            return null;

        IReadOnlyCollection<string> tenants = ClaimHelper.FindAll(context.User, [_options.TenantClaimType]);
        return tenants.Count == 1 ? tenants.First() : null;
    }
}
