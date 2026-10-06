using Can.Core.Domain.Results;
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
public sealed record RefreshTokenCommand(string RefreshToken, string? IpAddress = null) : IRequest<Result<AuthResult>>;

public sealed class RefreshTokenCommandHandler : IRequestHandler<RefreshTokenCommand, Result<AuthResult>>
{
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

    public async Task<Result<AuthResult>> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        string hash = _tokens.HashRefreshToken(request.RefreshToken);
        RefreshToken<Guid>? token = await _refreshTokens.GetAsync(t => t.TokenHash == hash, cancellationToken: cancellationToken);
        if (token is null)
            return AuthErrors.SessionExpired;

        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (token.IsRevoked)
        {
            await RevokeAllAsync(token.UserId, now, request.IpAddress, cancellationToken);
            return AuthErrors.SessionExpired;
        }

        if (token.IsExpired(now))
            return AuthErrors.SessionExpired;

        AppUser? user = await _identityStore.FindUserInAnyTenantAsync(token.UserId, cancellationToken);
        if (user is null)
            return AuthErrors.SessionExpired;

        TenantInfo? tenant = await _tenantStore.FindAsync(user.TenantId.ToString(), cancellationToken);
        if (tenant is not { IsActive: true } || user.IsLockedOut(now))
            return AuthErrors.SessionExpired;

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
public sealed record LogoutCommand(string? RefreshToken, string? IpAddress = null) : IRequest<Result<Success>>;

public sealed class LogoutCommandHandler : IRequestHandler<LogoutCommand, Result<Success>>
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

    public async Task<Result<Success>> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.RefreshToken))
            return Result.Success;

        string hash = _tokens.HashRefreshToken(request.RefreshToken);
        RefreshToken<Guid>? token = await _refreshTokens.GetAsync(t => t.TokenHash == hash, cancellationToken: cancellationToken);
        if (token is null || token.IsRevoked)
            return Result.Success;

        token.Revoke(_timeProvider.GetUtcNow(), request.IpAddress, "Çıkış yapıldı");
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success;
    }
}
