using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using CanAppTemplate.Application.Features.Auth.Rules;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Queries.GetProfile;

/// <summary>Giriş yapmış kullanıcının profili (<c>GET /api/auth/me</c>).</summary>
public sealed record GetProfileQuery : IRequest<UserProfileDto>, ISecuredRequest;

public sealed class GetProfileQueryHandler : IRequestHandler<GetProfileQuery, UserProfileDto>
{
    private readonly AuthBusinessRules _authBusinessRules;

    public GetProfileQueryHandler(AuthBusinessRules authBusinessRules) => _authBusinessRules = authBusinessRules;

    public async Task<UserProfileDto> Handle(GetProfileQuery request, CancellationToken cancellationToken)
    {
        AppUser user = await _authBusinessRules.CurrentUserShouldExist(q => q.WithRolesAndPermissions(), enableTracking: false, cancellationToken);
        return UserProfileDto.From(user);
    }
}
