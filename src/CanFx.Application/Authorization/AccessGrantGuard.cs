using CanFx.Application.Exceptions;

namespace CanFx.Application.Authorization;

/// <summary>Rol / yetki yönetimi kurallarının hata kodları (çeviri anahtarı olarak da kullanılabilir).</summary>
public static class AccessGrantErrorCodes
{
    /// <summary>Kendinde olmayan bir yetkiyi vermeye / almaya ya da kapsamadığı bir rolü atamaya çalıştı.</summary>
    public const string ExceedsOwn = "identity.grant_exceeds_own";

    /// <summary>Kendi rollerini ya da yetkilerini değiştirmeye çalıştı.</summary>
    public const string SelfModification = "identity.self_modification";

    /// <summary>Sistem rolü silinemez / adı değiştirilemez; Admin rolünün yetkileri düzenlenemez.</summary>
    public const string SystemRole = "identity.system_role";

    /// <summary>Son yönetici kaldırılamaz.</summary>
    public const string LastAdmin = "identity.last_admin";

    /// <summary>Kullanıcısı olan rol silinemez.</summary>
    public const string RoleInUse = "identity.role_in_use";

    /// <summary>Katalogda olmayan yetki.</summary>
    public const string UnknownPermission = "identity.unknown_permission";

    /// <summary>Koddan kaldırılmış (kullanımdan düşmüş) yetki verilemez.</summary>
    public const string DeprecatedPermission = "identity.deprecated_permission";
}

/// <summary>Rol üzerinde yapılan değişiklik.</summary>
public enum RoleChange
{
    /// <summary>Açıklama gibi bilgiler (sistem rollerinde de serbest).</summary>
    Details,
    Rename,
    Delete,
    Permissions,
}

/// <summary>
/// Rol / yetki yönetiminde yetki YÜKSELTMEYİ engelleyen kurallar. İsteği yapan kullanıcının (aktör) yetkileri
/// <see cref="ICurrentUser"/>'dan okunur; Admin rolündeki aktör her şeyi (<c>"*"</c>) kapsar.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>Alt küme:</b> kimse kendinde olmayan yetkiyi veremez / geri alamaz; rol atamak için rolün TÜM yetkilerini kapsamalı.</item>
/// <item><b>Kendini değiştirememe:</b> kimse kendi rol atamasını ve doğrudan yetkilerini değiştiremez; Admin olmayan aktör sahip olduğu rolü düzenleyemez.</item>
/// <item><b>Sistem rolleri:</b> silinemez, adı değişmez; Admin rolünün yetkileri düzenlenmez (zaten hepsini kapsar).</item>
/// <item><b>Son yönetici:</b> en az bir Admin kalmalı.</item>
/// </list>
/// "Yetki verme yetkisi" de sıradan bir yetkidir (<c>identity.roles.grant</c> vb.): Admin onu başka bir role verebilir;
/// o rol de yalnızca kendi kapsadığı yetkileri dağıtabilir.
/// </remarks>
public interface IAccessGrantGuard
{
    /// <summary>Aktörün etkin yetkileri (Admin ise yalnızca <c>"*"</c>).</summary>
    IReadOnlyCollection<string> ActorPermissions { get; }

    bool ActorIsAdmin { get; }

    /// <summary>Rol, yönetici rolü mü (<c>CanApplicationOptions.AdminRole</c>).</summary>
    bool IsAdminRole(string roleName);

    /// <summary>Yetki verme / geri alma: hepsi aktörün yetkilerince kapsanmalı.</summary>
    void EnsureCanGrant(IEnumerable<string> permissions);

    /// <summary>Rolü yeniden adlandırma, silme ya da yetkilerini değiştirme.</summary>
    void EnsureCanManageRole(string roleName, bool isSystemRole, RoleChange change);

    /// <summary>Kullanıcıya rol atama ya da kullanıcıdan rol alma.</summary>
    void EnsureCanAssignRole(string targetUserId, string roleName, IEnumerable<string> rolePermissions);

    /// <summary>Kullanıcıya doğrudan yetki verme ya da alma.</summary>
    void EnsureCanChangeUserPermissions(string targetUserId, IEnumerable<string> permissions);

    /// <summary>Değişiklikten sonra kalan aktif yönetici sayısı en az 1 olmalı.</summary>
    void EnsureAdminRemains(int adminCountAfterChange);
}

public sealed class AccessGrantGuard : IAccessGrantGuard
{
    private static readonly string[] Everything = [PermissionSet.All];

    private readonly ICurrentUser _currentUser;
    private readonly CanApplicationOptions _options;

    public AccessGrantGuard(ICurrentUser currentUser, CanApplicationOptions options)
    {
        _currentUser = currentUser;
        _options = options;
    }

    public bool ActorIsAdmin => _options.AdminRole is { Length: > 0 } adminRole && _currentUser.IsInRole(adminRole);

    public IReadOnlyCollection<string> ActorPermissions => ActorIsAdmin ? Everything : _currentUser.Permissions;

    public void EnsureCanGrant(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ActorId();

        IReadOnlyList<string> missing = PermissionSet.Missing(permissions, ActorPermissions);
        if (missing.Count > 0)
        {
            throw new ForbiddenException($"Sahip olmadığın yetkileri veremez ya da geri alamazsın: {string.Join(", ", missing)}.")
            {
                Code = AccessGrantErrorCodes.ExceedsOwn,
            };
        }
    }

    public void EnsureCanManageRole(string roleName, bool isSystemRole, RoleChange change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        ActorId();

        if (isSystemRole && change is RoleChange.Rename or RoleChange.Delete)
            throw new ConflictException($"'{roleName}' bir sistem rolü; silinemez ve adı değiştirilemez.") { Code = AccessGrantErrorCodes.SystemRole };

        if (change == RoleChange.Permissions && IsAdminRole(roleName))
            throw new ConflictException($"'{roleName}' rolü zaten tüm yetkileri kapsar; yetkileri düzenlenemez.") { Code = AccessGrantErrorCodes.SystemRole };

        if (!ActorIsAdmin && _currentUser.IsInRole(roleName))
            throw new ForbiddenException($"Sahip olduğun '{roleName}' rolünü değiştiremezsin.") { Code = AccessGrantErrorCodes.SelfModification };
    }

    public void EnsureCanAssignRole(string targetUserId, string roleName, IEnumerable<string> rolePermissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleName);
        ArgumentNullException.ThrowIfNull(rolePermissions);
        EnsureNotSelf(targetUserId);
        EnsureCanGrant(IsAdminRole(roleName) ? Everything : rolePermissions);
    }

    public void EnsureCanChangeUserPermissions(string targetUserId, IEnumerable<string> permissions)
    {
        EnsureNotSelf(targetUserId);
        EnsureCanGrant(permissions);
    }

    public void EnsureAdminRemains(int adminCountAfterChange)
    {
        if (adminCountAfterChange < 1)
            throw new ConflictException("Son yönetici kaldırılamaz; önce başka bir kullanıcıyı yönetici yap.") { Code = AccessGrantErrorCodes.LastAdmin };
    }

    public bool IsAdminRole(string roleName) =>
        _options.AdminRole is { Length: > 0 } adminRole && string.Equals(roleName, adminRole, StringComparison.OrdinalIgnoreCase);

    private void EnsureNotSelf(string targetUserId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetUserId);
        if (string.Equals(ActorId(), targetUserId, StringComparison.OrdinalIgnoreCase))
            throw new ForbiddenException("Kendi rollerini ve yetkilerini değiştiremezsin.") { Code = AccessGrantErrorCodes.SelfModification };
    }

    private string ActorId() => _currentUser.Id ?? throw new UnauthorizedException();
}
