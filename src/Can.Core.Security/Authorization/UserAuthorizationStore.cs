namespace Can.Core.Security.Authorization;

/// <summary>Kullanıcının o anki rolleri ve etkin yetkileri (rollerinden gelenler + doğrudan verilenler).</summary>
public sealed record UserAuthorizationData(IReadOnlyList<string> Roles, IReadOnlyList<string> Permissions);

/// <summary>
/// Kullanıcının rollerini ve yetkilerini SUNUCU tarafında okur. Böylece rol / yetki değişikliği token süresini beklemeden
/// yansır ve yetkiler token'a (cookie'ye) yazılmak zorunda kalmaz. WebApi: <c>UseCanPermissionResolution()</c>.
/// EF Core uygulaması: <c>Can.Core.Security.EntityFrameworkCore</c> → <c>AddCanUserAuthorizationStore&lt;...&gt;()</c>.
/// </summary>
/// <remarks>
/// Her istekte çağrılır: uygulama kısa süreli önbellek kullanmalı. Değişiklikten sonra <see cref="Invalidate"/>
/// (kullanıcının rolleri / doğrudan yetkileri değişti) ya da <see cref="InvalidateAll"/> (bir rolün yetkileri değişti,
/// rol silindi / yeniden adlandırıldı) çağrılmalı: aynı sunucuda hemen, diğer sunucularda önbellek süresi sonunda yansır.
/// </remarks>
public interface IUserAuthorizationStore
{
    /// <summary>Kullanıcı bulunamazsa <see langword="null"/>.</summary>
    ValueTask<UserAuthorizationData?> GetAsync(string userId, CancellationToken cancellationToken = default);

    void Invalidate(string userId);

    void InvalidateAll();
}
