using Can.Core.Domain.Results;
using Can.Core.MultiTenancy;

namespace Northwind.Application.Common;

/// <summary>
/// Giriş yapılmamış isteklerde (giriş, kayıt, herkese açık katalog) mağazayı istemcinin verdiği kısa addan seçer.
/// Giriş yapılmış isteklerde mağaza her zaman token'dan gelir; bu sınıf yalnızca anonim akışlar içindir.
/// </summary>
public sealed class StoreTenant
{
    private readonly ITenantStore _tenantStore;
    private readonly TenantContext _tenantContext;

    public StoreTenant(ITenantStore tenantStore, TenantContext tenantContext)
    {
        _tenantStore = tenantStore;
        _tenantContext = tenantContext;
    }

    /// <summary>Mağazayı bulur ve bu isteğin tenant'ı yapar; yoksa ya da pasifse 404.</summary>
    public async Task<Result<TenantInfo>> UseAsync(string identifier, CancellationToken cancellationToken)
    {
        TenantInfo? tenant = await _tenantStore.FindAsync(identifier.Trim(), cancellationToken);
        if (tenant is not { IsActive: true })
            return NotFound(identifier);

        _tenantContext.Set(tenant);
        return tenant;
    }

    public static Error NotFound(string identifier) => Error.NotFound("store.not_found", $"'{identifier}' adında bir mağaza bulunamadı.");
}
