using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Queries.GetProfile;

/// <summary>Giriş yapmış kullanıcının profili (<c>GET /api/auth/me</c>).</summary>
public sealed record GetProfileQuery : IRequest<Result<UserProfileDto>>, ISecuredRequest;

public sealed class GetProfileQueryHandler : IRequestHandler<GetProfileQuery, Result<UserProfileDto>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;

    public GetProfileQueryHandler(ICurrentUser currentUser, IRepository<AppUser, Guid> users)
    {
        _currentUser = currentUser;
        _users = users;
    }

    public async Task<Result<UserProfileDto>> Handle(GetProfileQuery request, CancellationToken cancellationToken)
    {
        Result<Guid> userId = _currentUser.UserId();
        if (userId.IsFailure)
            return userId.Errors;

        AppUser? user = await _users.GetByIdAsync(userId.Value, include: q => q.WithRolesAndPermissions(), enableTracking: false, cancellationToken: cancellationToken);
        if (user is null)
            return Error.Unauthorized();

        return UserProfileDto.From(user);
    }
}
