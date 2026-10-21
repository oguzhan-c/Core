using CanFx.Application.Authorization;

namespace CanAppTemplate.Application.Features.Roles.Constants;

public static class RolesBusinessMessages
{
    public const string RoleNotExists = "Rol bulunamadı.";
    public const string RoleNotExistsCode = "role.not_exists";

    public const string RoleNameExists = "Bu adda bir rol zaten var.";
    public const string RoleNameExistsCode = "role.name_exists";

    public const string RoleInUse = "Bu rol kullanıcılara atanmış; önce kullanıcılardan kaldır.";
    public const string RoleInUseCode = AccessGrantErrorCodes.RoleInUse;

    public const string HostOnly = "Roller ve rol yetkileri yalnızca ana (host) yönetimden düzenlenebilir.";
    public const string HostOnlyCode = "identity.host_only";
}
