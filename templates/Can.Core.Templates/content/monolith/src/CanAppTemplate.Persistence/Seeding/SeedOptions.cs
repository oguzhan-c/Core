namespace CanAppTemplate.Persistence.Seeding;

/// <summary>
/// Başlangıç verisi ayarları (appsettings: <c>Seed</c>). Şifreler appsettings'e YAZILMAZ; user-secrets'tan gelir:
/// <code>
/// dotnet user-secrets set "Seed:AdminPassword" "..." --project src/CanAppTemplate.WebApi
/// </code>
/// </summary>
public sealed class SeedOptions
{
    /// <summary>TODO(can-gen): Yönetici e-postasını kendi alan adınla değiştir.</summary>
    public string AdminEmail { get; set; } = "admin@canapptemplate.local";

    /// <summary>
    /// Yönetici şifresi (user-secrets). Boşsa yönetici hesabı OLUŞTURULMAZ ve uyarı loglanır: rastgele şifre üretip
    /// loga yazmak hem güvensiz hem de log kaybolunca hesaba girilemez.
    /// </summary>
    public string? AdminPassword { get; set; }
}
