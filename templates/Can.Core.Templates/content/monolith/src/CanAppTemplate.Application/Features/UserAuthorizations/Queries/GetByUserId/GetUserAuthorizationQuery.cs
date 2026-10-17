using Can.Core.Application;
using Can.Core.Mediator;
using Can.Core.Security.Authorization;
using CanAppTemplate.Application.Features.UserAuthorizations.Rules;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.UserAuthorizations.Queries.GetByUserId;

/// <summary>Kullanıcının rolleri, doğrudan yetkileri ve etkin yetkileri.</summary>
public sealed class GetUserAuthorizationQuery : IRequest<UserAuthorizationResponse>, ISecuredRequest
{
    public Guid UserId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [IdentityPermissions.UsersRead];
}

public sealed record UserAuthorizationItem(Guid Id, string Name);

public sealed record UserAuthorizationResponse(
    Guid UserId,
    string Email,
    IReadOnlyList<UserAuthorizationItem> Roles,
    IReadOnlyList<UserAuthorizationItem> DirectPermissions,
    IReadOnlyList<string> EffectivePermissions)
{
    /// <remarks>Roller ve yetkiler yüklenmiş olmalı.</remarks>
    public static UserAuthorizationResponse From(AppUser user) =>
        new(
            user.Id,
            user.Email,
            user.UserRoles.Where(ur => ur.Role is not null).Select(ur => new UserAuthorizationItem(ur.RoleId, ur.Role!.Name)).OrderBy(r => r.Name, StringComparer.Ordinal).ToArray(),
            user.OperationClaims.Where(uc => uc.OperationClaim is not null).Select(uc => new UserAuthorizationItem(uc.OperationClaimId, uc.OperationClaim!.Name)).OrderBy(c => c.Name, StringComparer.Ordinal).ToArray(),
            user.GetPermissionNames()
        );
}

public sealed class GetUserAuthorizationQueryHandler : IRequestHandler<GetUserAuthorizationQuery, UserAuthorizationResponse>
{
    private readonly UserAuthorizationBusinessRules _rules;

    public GetUserAuthorizationQueryHandler(UserAuthorizationBusinessRules rules) => _rules = rules;

    public async Task<UserAuthorizationResponse> Handle(GetUserAuthorizationQuery request, CancellationToken cancellationToken) =>
        UserAuthorizationResponse.From(await _rules.UserIdShouldExistWhenSelected(request.UserId, cancellationToken));
}
