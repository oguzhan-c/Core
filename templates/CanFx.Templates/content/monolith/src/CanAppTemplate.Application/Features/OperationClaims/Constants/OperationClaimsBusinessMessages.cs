using CanFx.Application.Authorization;

namespace CanAppTemplate.Application.Features.OperationClaims.Constants;

public static class OperationClaimsBusinessMessages
{
    public const string OperationClaimNotExists = "Yetkilerden biri ya da birkaçı bulunamadı.";
    public const string OperationClaimNotExistsCode = AccessGrantErrorCodes.UnknownPermission;

    public const string OperationClaimDeprecated = "Kullanımdan kaldırılmış yetki verilemez.";
    public const string OperationClaimDeprecatedCode = AccessGrantErrorCodes.DeprecatedPermission;
}
