namespace CanFx.Security.Tokens;

/// <summary>
/// Token'daki güvenlik damgasının (<see cref="TokenService.SecurityStampClaimType"/>) hâlâ geçerli olup olmadığını söyler.
/// EF Core uygulaması: <c>CanFx.Security.EntityFrameworkCore</c> → <c>AddCanSecurityStampValidator&lt;...&gt;()</c>.
/// </summary>
/// <remarks>
/// Her istekte çağrılır: uygulama kısa süreli önbellek kullanmalı (damga değişikliği en geç bu süre sonra
/// yansır). Kullanıcı bulunamazsa (silinmiş) <see langword="false"/> dönmeli.
/// </remarks>
public interface ISecurityStampValidator
{
    ValueTask<bool> IsCurrentAsync(string userId, string securityStamp, CancellationToken cancellationToken = default);

    /// <summary>Damga değiştiğinde bu sunucudaki önbelleği hemen temizler (diğer sunucular önbellek süresi sonunda görür).</summary>
    void Invalidate(string userId);
}
