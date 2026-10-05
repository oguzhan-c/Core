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
    /// Kullanıcının üye olduğu tenant'ları taşıyan claim (birden fazla değer olabilir).
    /// Tenant HER ZAMAN doğrulanmış token'dan gelir.
    /// </summary>
    public string TenantClaimType { get; set; } = "tenant_id";

    /// <summary>
    /// Birden fazla tenant'a üye kullanıcıların aktif tenant'ı seçtiği header (ör. <c>"X-Tenant-Id"</c>).
    /// Varsayılan <see langword="null"/>: kapalı. Açıksa header'daki değer kullanıcının tenant claim'lerinden
    /// biri olmak zorundadır; değilse istek 403 ile reddedilir.
    /// </summary>
    public string? TenantHeaderName { get; set; }

    /// <summary>Header ile herhangi bir tenant'ı seçebilen rol (ör. sistem yöneticisi). Varsayılan: kapalı.</summary>
    public string? TenantAdminRole { get; set; }

    /// <summary>
    /// Claim/header'daki metni entity'lerdeki <c>TenantId</c> tipine çevirir. Varsayılan: <see cref="Guid"/>.
    /// int kullanıyorsan: <c>v =&gt; int.TryParse(v, out int id) ? id : null</c>.
    /// </summary>
    public Func<string, object?> TenantIdParser { get; set; } = value => Guid.TryParse(value, out Guid id) ? id : null;
}
