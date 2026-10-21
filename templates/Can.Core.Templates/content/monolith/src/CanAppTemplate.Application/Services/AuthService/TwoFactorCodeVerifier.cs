using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Otp;
using Can.Core.Security.RecoveryCodes;
using Can.Core.Security.Sessions;
using FluentValidation;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Services.AuthService;

internal static class TwoFactorChallenges
{
    /// <summary>Bekleyen girişin kullanıcısını yükler; şifre/2FA ayarı o arada değiştiyse geçersiz sayar.</summary>
    public static async Task<Result<AppUser>> ResolveUserAsync(IRepository<AppUser, IdentityKey> users, TwoFactorChallenge challenge, CancellationToken cancellationToken)
    {
        AppUser? user = await users.GetByIdAsync(challenge.UserId, include: q => q.WithRolesAndPermissions(), cancellationToken: cancellationToken);

        if (user is null || user.SecurityStamp != challenge.SecurityStamp || user.AuthenticatorType != challenge.Method)
            return AuthErrors.TwoFactorExpired;

        return user;
    }
}

/// <summary>Kullanıcının seçtiği yönteme göre (authenticator uygulaması / e-posta) ikinci adım kodunu doğrular.</summary>
public sealed class TwoFactorCodeVerifier
{
    private readonly IRepository<OtpAuthenticator<IdentityKey>, IdentityKey> _otpAuthenticators;
    private readonly ITotpService _totp;
    private readonly EmailVerification _emailVerification;

    public TwoFactorCodeVerifier(IRepository<OtpAuthenticator<IdentityKey>, IdentityKey> otpAuthenticators, ITotpService totp, EmailVerification emailVerification)
    {
        _otpAuthenticators = otpAuthenticators;
        _totp = totp;
        _emailVerification = emailVerification;
    }

    public async Task<bool> VerifyAsync(AppUser user, string code, CancellationToken cancellationToken) =>
        user.AuthenticatorType switch
        {
            AuthenticatorType.Otp => await VerifyOtpAsync(user.Id, code, requireVerified: true, cancellationToken) is not null,
            AuthenticatorType.Email => await _emailVerification.VerifyAsync(user, code, cancellationToken),
            _ => false,
        };

    /// <summary>
    /// Authenticator uygulamasının kodunu doğrular; başarılıysa kaydı döndürür (kod tekrar kullanılamaz).
    /// <paramref name="requireVerified"/> <see langword="false"/>: kurulumun son adımı (ilk kod).
    /// </summary>
    public async Task<OtpAuthenticator<IdentityKey>?> VerifyOtpAsync(IdentityKey userId, string code, bool requireVerified, CancellationToken cancellationToken)
    {
        OtpAuthenticator<IdentityKey>? authenticator = await _otpAuthenticators.GetAsync(a => a.UserId == userId, cancellationToken: cancellationToken);
        if (authenticator is null || (requireVerified && !authenticator.IsVerified))
            return null;

        if (!_totp.TryVerify(authenticator.SecretKey, code, authenticator.LastUsedTimeStep, out long timeStep))
            return null;

        authenticator.MarkCodeUsed(timeStep);
        return authenticator;
    }
}
