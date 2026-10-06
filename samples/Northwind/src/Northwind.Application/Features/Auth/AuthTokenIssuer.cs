using Can.Core.MultiTenancy;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Tokens;
using Northwind.Domain.Identity;

namespace Northwind.Application.Features.Auth;

/// <summary>Kullanıcı için access token + refresh token üretir; refresh token'ın hash'ini kaydedilmek üzere ekler.</summary>
public sealed class AuthTokenIssuer
{
    private readonly ITokenService _tokens;
    private readonly IRepository<RefreshToken<Guid>, Guid> _refreshTokens;
    private readonly TenantContext _tenantContext;
    private readonly TimeProvider _timeProvider;

    public AuthTokenIssuer(
        ITokenService tokens,
        IRepository<RefreshToken<Guid>, Guid> refreshTokens,
        TenantContext tenantContext,
        TimeProvider timeProvider)
    {
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _tenantContext = tenantContext;
        _timeProvider = timeProvider;
    }

    /// <remarks><paramref name="user"/>'ın rolleri ve yetkileri (<c>WithRolesAndPermissions</c>) yüklenmiş ve tenant bağlamı ayarlanmış olmalı.</remarks>
    public async Task<AuthResult> IssueAsync(AppUser user, string? ipAddress, CancellationToken cancellationToken)
    {
        UserProfileDto profile = UserProfileDto.From(user, _tenantContext.Tenant);

        AccessToken accessToken = _tokens.CreateAccessToken(
            new TokenSubject(
                UserId: user.Id.ToString(),
                UserName: $"{user.FirstName} {user.LastName}",
                Email: user.Email,
                Roles: profile.Roles,
                TenantId: user.TenantId.ToString(),
                Permissions: profile.Permissions
            )
        );

        RefreshTokenValue refreshToken = _tokens.CreateRefreshToken();
        await _refreshTokens.AddAsync(new RefreshToken<Guid>(user.Id, refreshToken, _timeProvider.GetUtcNow(), ipAddress), cancellationToken);

        return new AuthResult(accessToken, refreshToken, profile);
    }
}
