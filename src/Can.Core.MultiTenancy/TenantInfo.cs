namespace Can.Core.MultiTenancy;

/// <summary>
/// Bir tenant'ın tanımı. Her tenant ya ortak veritabanını (<see cref="ConnectionString"/> boş) ya da
/// kendine ait bir veritabanını kullanır; iki yaklaşım aynı uygulamada birlikte çalışabilir.
/// </summary>
public sealed class TenantInfo
{
    /// <summary>
    /// Kalıcı kimlik; entity'lerdeki <c>TenantId</c> ve token'daki <c>tenant_id</c> claim'i ile aynı değer
    /// (ör. Guid'in metin hâli).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>İnsan okunur kısa ad (ör. <c>"acme"</c>); header ya da adres ile seçimde kullanılabilir.</summary>
    public string Identifier { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Tenant'a özel veritabanı. Boşsa tenant ortak veritabanını kullanır ve verileri <c>TenantId</c>
    /// filtresiyle ayrılır.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Pasif tenant'ın kullanıcıları erişemez.</summary>
    public bool IsActive { get; set; } = true;

    public bool HasDedicatedDatabase => !string.IsNullOrWhiteSpace(ConnectionString);

    public override string ToString() => string.IsNullOrEmpty(Identifier) ? Id : $"{Identifier} ({Id})";
}
