using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Tokens;
using CanAppTemplate.Application.Common;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Application.Features.Auth;

/// <summary>
/// Kullanıcıya access + refresh token üretir. Her giriş yeni bir oturum (<c>sid</c>) başlatır; yenilemede aynı oturum
/// sürer. Token'a güvenlik damgası yazılır: şifre/2FA değişince eski token'lar süresi dolmadan geçersiz olur.
#if (MultiTenancy)
/// Kullanıcının tenant'ı token'a <c>tenant_id</c> olarak yazılır; istekte tenant bu claim'den çözülür.
#endif
/// </summary>
public sealed class AuthTokenIssuer
{
    private readonly ITokenService _tokens;
    private readonly IRepository<RefreshToken<Guid>, Guid> _refreshTokens;
    private readonly TimeProvider _timeProvider;

    public AuthTokenIssuer(
        ITokenService tokens,
        IRepository<RefreshToken<Guid>, Guid> refreshTokens,
        TimeProvider timeProvider)
    {
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _timeProvider = timeProvider;
    }

    /// <summary>Yeni oturum (giriş).</summary>
    /// <remarks><paramref name="user"/>'ın rolleri ve yetkileri (<c>WithRolesAndPermissions</c>) yüklenmiş olmalı.</remarks>
    public async Task<AuthResult> IssueAsync(AppUser user, ClientInfo client, CancellationToken cancellationToken)
    {
        RefreshTokenValue refreshToken = _tokens.CreateRefreshToken();
        var session = new RefreshToken<Guid>(
            user.Id,
            refreshToken,
            _timeProvider.GetUtcNow(),
            client.IpAddress,
            user.SecurityStamp,
            client.ToSessionDevice()
        );
        await _refreshTokens.AddAsync(session, cancellationToken);

        return await CreateAsync(user, refreshToken, session.SessionId, cancellationToken);
    }

    /// <summary>Yenileme: eski refresh token "rotated" olarak kapanır, aynı oturum yeni token'la sürer.</summary>
    public async Task<AuthResult> RotateAsync(AppUser user, RefreshToken<Guid> current, ClientInfo client, CancellationToken cancellationToken)
    {
        RefreshTokenValue refreshToken = _tokens.CreateRefreshToken();
        RefreshToken<Guid> next = current.Rotate(refreshToken, _timeProvider.GetUtcNow(), client.IpAddress, user.SecurityStamp, client.UserAgent);
        await _refreshTokens.AddAsync(next, cancellationToken);

        return await CreateAsync(user, refreshToken, next.SessionId, cancellationToken);
    }

    private Task<AuthResult> CreateAsync(AppUser user, RefreshTokenValue refreshToken, string sessionId, CancellationToken cancellationToken)
    {
#if (MultiTenancy)
        // TODO(can-gen): Tenant askıya alınabiliyorsa pasif tenant'ı token'a yazma (tenant deposundan durumunu kontrol et).
        string? tenantId = user.TenantId?.ToString();
#else
        string? tenantId = null;
#endif
        UserProfileDto profile = UserProfileDto.From(user);
        AccessToken accessToken = _tokens.CreateAccessToken(
            new TokenSubject(
                UserId: user.Id.ToString(),
                UserName: user.FullName,
                Email: user.Email,
                Roles: profile.Roles,
                TenantId: tenantId,
                Permissions: profile.Permissions,
                SecurityStamp: user.SecurityStamp,
                SessionId: sessionId
            )
        );

        return Task.FromResult(new AuthResult(accessToken, refreshToken, profile, sessionId));
    }
}
