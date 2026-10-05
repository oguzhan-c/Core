using Can.Core.Application.Exceptions;
using Can.Core.Mediator;
using Can.Core.MultiTenancy;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Tokens;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Common;
using Northwind.Domain.Identity;

namespace Northwind.Application.Features.Auth;

/// <summary>
/// Refresh token ile yeni token çifti alır (rotasyon). İptal edilmiş bir token tekrar kullanılırsa token çalınmış
/// sayılır ve kullanıcının tüm oturumları kapatılır.
/// </summary>
public sealed record RefreshTokenCommand(string RefreshToken, string? IpAddress = null) : IRequest<AuthResult>;

public sealed class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, AuthResult>
{
    private const string InvalidToken = "Oturumun süresi doldu; tekrar giriş yap.";

    private readonly ITokenService _tokens;
    private readonly IRepository<RefreshToken<Guid>, Guid> _refreshTokens;
    private readonly IIdentityStore _identityStore;
    private readonly ITenantStore _tenantStore;
    private readonly TenantContext _tenantContext;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public RefreshTokenCommandHandler(
        ITokenService tokens,
        IRepository<RefreshToken<Guid>, Guid> refreshTokens,
        IIdentityStore identityStore,
        ITenantStore tenantStore,
        TenantContext tenantContext,
        AuthTokenIssuer tokenIssuer,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _identityStore = identityStore;
        _tenantStore = tenantStore;
        _tenantContext = tenantContext;
        _tokenIssuer = tokenIssuer;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<AuthResult> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        string hash = _tokens.HashRefreshToken(request.RefreshToken);
        RefreshToken<Guid> token =
            await _refreshTokens.GetAsync(t => t.TokenHash == hash, cancellationToken: cancellationToken)
            ?? throw new UnauthorizedException(InvalidToken);

        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (token.IsRevoked)
        {
            await RevokeAllAsync(token.UserId, now, request.IpAddress, cancellationToken);
            throw new UnauthorizedException(InvalidToken);
        }

        if (token.IsExpired(now))
            throw new UnauthorizedException(InvalidToken);

        AppUser user =
            await _identityStore.FindUserInAnyTenantAsync(token.UserId, cancellationToken)
            ?? throw new UnauthorizedException(InvalidToken);

        TenantInfo? tenant = await _tenantStore.FindAsync(user.TenantId.ToString(), cancellationToken);
        if (tenant is not { IsActive: true } || user.IsLockedOut(now))
            throw new UnauthorizedException(InvalidToken);

        _tenantContext.Set(tenant);

        AuthResult result = await _tokenIssuer.IssueAsync(user, request.IpAddress, cancellationToken);
        token.Revoke(now, request.IpAddress, "Yenilendi", result.RefreshToken.TokenHash);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return result;
    }

    private async Task RevokeAllAsync(Guid userId, DateTimeOffset now, string? ipAddress, CancellationToken cancellationToken)
    {
        List<RefreshToken<Guid>> active = await _refreshTokens
            .Query()
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (RefreshToken<Guid> token in active)
            token.Revoke(now, ipAddress, "Yeniden kullanım tespit edildi");

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Çıkış: refresh token iptal edilir (cookie'leri WebApi siler).</summary>
public sealed record LogoutCommand(string? RefreshToken, string? IpAddress = null) : IRequest;

public sealed class LogoutCommandHandler : IRequestHandler<LogoutCommand>
{
    private readonly ITokenService _tokens;
    private readonly IRepository<RefreshToken<Guid>, Guid> _refreshTokens;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public LogoutCommandHandler(ITokenService tokens, IRepository<RefreshToken<Guid>, Guid> refreshTokens, IUnitOfWork unitOfWork, TimeProvider timeProvider)
    {
        _tokens = tokens;
        _refreshTokens = refreshTokens;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
            return;

        string hash = _tokens.HashRefreshToken(request.RefreshToken);
        RefreshToken<Guid>? token = await _refreshTokens.GetAsync(t => t.TokenHash == hash, cancellationToken: cancellationToken);
        if (token is null || token.IsRevoked)
            return;

        token.Revoke(_timeProvider.GetUtcNow(), request.IpAddress, "Çıkış yapıldı");
        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
