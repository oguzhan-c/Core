namespace CanAppTemplate.Application.Features.UserAuthorizations.Commands.RevokePermissions;

/// <summary>İşlemden sonra kullanıcının doğrudan verilmiş yetkileri (rollerinden gelenler hariç).</summary>
public sealed record RevokedUserPermissionsResponse(IdentityKey UserId, IReadOnlyList<string> DirectPermissions);
