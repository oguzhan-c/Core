using Can.Core.Application;
using Can.Core.Mediator;
using CanAppTemplate.Application.Features.UserAuthorizations.Constants;
using CanAppTemplate.Application.Features.UserAuthorizations.Rules;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.UserAuthorizations.Queries.GetByUserId;

/// <summary>Kullanıcının rolleri, doğrudan yetkileri ve etkin yetkileri.</summary>
public sealed class GetByUserIdUserAuthorizationQuery : IRequest<GetByUserIdUserAuthorizationResponse>, ISecuredRequest
{
    public IdentityKey UserId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [UserAuthorizationsOperationClaims.Read];
}

public sealed class GetByUserIdUserAuthorizationQueryHandler : IRequestHandler<GetByUserIdUserAuthorizationQuery, GetByUserIdUserAuthorizationResponse>
{
    private readonly UserAuthorizationBusinessRules _userAuthorizationBusinessRules;

    public GetByUserIdUserAuthorizationQueryHandler(UserAuthorizationBusinessRules userAuthorizationBusinessRules) =>
        _userAuthorizationBusinessRules = userAuthorizationBusinessRules;

    public async Task<GetByUserIdUserAuthorizationResponse> Handle(GetByUserIdUserAuthorizationQuery request, CancellationToken cancellationToken)
    {
        AppUser user = await _userAuthorizationBusinessRules.UserIdShouldExistWhenSelected(request.UserId, cancellationToken);
        return new GetByUserIdUserAuthorizationResponse(
            user.Id,
            user.Email,
            user.UserRoles.Where(ur => ur.Role is not null).Select(ur => new UserAuthorizationItemDto(ur.RoleId, ur.Role!.Name)).OrderBy(r => r.Name, StringComparer.Ordinal).ToArray(),
            user.OperationClaims.Where(uc => uc.OperationClaim is not null).Select(uc => new UserAuthorizationItemDto(uc.OperationClaimId, uc.OperationClaim!.Name)).OrderBy(c => c.Name, StringComparer.Ordinal).ToArray(),
            user.GetPermissionNames()
        );
    }
}
