namespace CanAppTemplate.Application.Features.UserAuthorizations.Queries.GetByUserId;

public sealed record GetByUserIdUserAuthorizationResponse(
    IdentityKey UserId,
    string Email,
    IReadOnlyList<UserAuthorizationItemDto> Roles,
    IReadOnlyList<UserAuthorizationItemDto> DirectPermissions,
    IReadOnlyList<string> EffectivePermissions);

public sealed record UserAuthorizationItemDto(IdentityKey Id, string Name);
