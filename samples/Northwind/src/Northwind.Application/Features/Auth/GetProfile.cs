using Can.Core.Application;
using Can.Core.Application.Exceptions;
using Can.Core.Mediator;
using Can.Core.MultiTenancy;
using Can.Core.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Northwind.Domain.Identity;

namespace Northwind.Application.Features.Auth;

/// <summary>Giriş yapmış kullanıcının profili (<c>GET /auth/me</c>).</summary>
public sealed record GetProfileQuery : IRequest<UserProfileDto>, ISecuredRequest;

public sealed class GetProfileQueryHandler : IRequestHandler<GetProfileQuery, UserProfileDto>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly TenantContext _tenantContext;

    public GetProfileQueryHandler(ICurrentUser currentUser, IRepository<AppUser, Guid> users, TenantContext tenantContext)
    {
        _currentUser = currentUser;
        _users = users;
        _tenantContext = tenantContext;
    }

    public async Task<UserProfileDto> Handle(GetProfileQuery request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(_currentUser.Id, out Guid userId))
            throw new UnauthorizedException();

        AppUser user =
            await _users.GetByIdAsync(
                userId,
                include: q => q.Include(u => u.UserRoles).ThenInclude(ur => ur.Role),
                enableTracking: false,
                cancellationToken: cancellationToken
            ) ?? throw new UnauthorizedException();

        return UserProfileDto.From(user, _tenantContext.Tenant);
    }
}
