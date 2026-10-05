using Can.Core.Application;

namespace Can.Core.MultiTenancy;

/// <summary>
/// Bu DI scope'unun (HTTP isteği, arka plan işi ...) aktif tenant'ı. HTTP'de
/// <c>UseCanTenantResolution()</c> doldurur; arka plan işlerinde <c>CreateTenantScope(tenant)</c> ile verilir.
/// </summary>
public sealed class TenantContext
{
    /// <summary>Aktif tenant'ın kimliği (metin).</summary>
    public string? TenantId { get; private set; }

    /// <summary>Tenant deposundan yüklenmiş tanım (depo kullanılmıyorsa <see langword="null"/>).</summary>
    public TenantInfo? Tenant { get; private set; }

    public void Set(TenantInfo tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        Tenant = tenant;
        TenantId = tenant.Id;
    }

    /// <summary>Depo olmadan, yalnızca kimlikle (ör. token'daki claim).</summary>
    public void Set(string? tenantId)
    {
        Tenant = null;
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId;
    }

    public void Clear() => Set((string?)null);
}

/// <summary>
/// <see cref="ICurrentTenant"/>: <see cref="TenantContext"/>'teki kimliği entity'lerdeki <c>TenantId</c>
/// tipine çevirir (<see cref="MultiTenancyOptions.TenantIdParser"/>).
/// </summary>
public sealed class CurrentTenant : ICurrentTenant
{
    private readonly TenantContext _context;
    private readonly MultiTenancyOptions _options;

    public CurrentTenant(TenantContext context, MultiTenancyOptions options)
    {
        _context = context;
        _options = options;
    }

    public object? Id => _context.TenantId is { } id ? _options.TenantIdParser(id) : null;
}

/// <summary>Aktif tenant'ın kullanacağı veritabanı bağlantı dizesi.</summary>
public interface ITenantConnectionStringResolver
{
    /// <summary>
    /// Tenant'ın kendi veritabanı varsa onu, yoksa <see cref="MultiTenancyOptions.DefaultConnectionString"/>
    /// (ortak veritabanı) döndürür.
    /// </summary>
    string Resolve();
}

public sealed class TenantConnectionStringResolver : ITenantConnectionStringResolver
{
    private readonly TenantContext _context;
    private readonly MultiTenancyOptions _options;

    public TenantConnectionStringResolver(TenantContext context, MultiTenancyOptions options)
    {
        _context = context;
        _options = options;
    }

    public string Resolve()
    {
        if (_context.Tenant is { HasDedicatedDatabase: true } tenant)
            return tenant.ConnectionString!;

        // Tenant'a ayrı veritabanı tanımlanmışsa ama tenant tanımı yüklenmemişse ortak veritabanına
        // düşmek veri karışmasına yol açar; bu yüzden depo kullanan kurulumlarda tanım mutlaka yüklenmeli.
        return _options.DefaultConnectionString
            ?? throw new InvalidOperationException(
                "Bağlantı dizesi bulunamadı: aktif tenant'ın kendi veritabanı yok ve MultiTenancyOptions.DefaultConnectionString boş."
            );
    }
}
