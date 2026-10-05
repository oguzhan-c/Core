using System.Security.Claims;

namespace Can.Core.WebApi;

/// <summary><c>AddCanWebApi(...)</c> ayarları.</summary>
public sealed class CanWebApiOptions
{
    // ---------------------------------------------------------------- Kullanıcı claim'leri
    // Birden fazla tip verilebilir; ilk bulunan kullanılır. JWT'nin ham ("sub") ve
    // ASP.NET'in eşlenmiş (ClaimTypes.NameIdentifier) adları varsayılan olarak desteklenir.

    public string[] UserIdClaimTypes { get; set; } = [ClaimTypes.NameIdentifier, "sub"];

    public string[] UserNameClaimTypes { get; set; } = [ClaimTypes.Name, "name", "preferred_username"];

    public string[] EmailClaimTypes { get; set; } = [ClaimTypes.Email, "email"];

    public string[] RoleClaimTypes { get; set; } = [ClaimTypes.Role, "role", "roles"];

    // ---------------------------------------------------------------- Tenant

    /// <summary>
    /// Oturumun aktif tenant'ını taşıyan claim. Tenant YALNIZCA doğrulanmış (imzalı) token'dan okunur;
    /// header, query string ya da istek gövdesinden okunmaz. Token'da birden fazla değer varsa tenant
    /// belirsiz sayılır ve tenant'a ait hiçbir veri görünmez.
    /// </summary>
    public string TenantClaimType { get; set; } = "tenant_id";
}
