namespace CanAppTemplate.Application.Features.UserAuthorizations.Commands.GrantPermissions;

/// <summary>İşlemden sonra kullanıcının doğrudan verilmiş yetkileri (rollerinden gelenler hariç).</summary>
public sealed record GrantedUserPermissionsResponse(IdentityKey UserId, IReadOnlyList<string> DirectPermissions);
