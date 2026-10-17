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
using CanAppTemplate.Application.Common;
using CanAppTemplate.Domain.Identity;

namespace CanAppTemplate.Application.Features.Auth;

// ---------------------------------------------------------------- ikinci adım

/// <summary>
/// İki adımlı girişin ikinci adımı: authenticator uygulamasındaki ya da e-postaya gelen 6 haneli kod, ya da cihaz
/// kaybolduysa kurtarma kodlarından biri. <see cref="Challenge"/> sunucunun şifreleyip cookie'de sakladığı bilgidir;
/// istemci değiştiremez.
/// </summary>
public sealed record CompleteTwoFactorLoginCommand(TwoFactorChallenge Challenge, string Code, bool IsRecoveryCode, ClientInfo Client)
    : IRequest<Result<AuthResult>>, ILoggableRequest;

public sealed class CompleteTwoFactorLoginCommandValidator : AbstractValidator<CompleteTwoFactorLoginCommand>
{
    public CompleteTwoFactorLoginCommandValidator()
    {
        RuleFor(c => c.Code).SixDigitCode().When(c => !c.IsRecoveryCode);
        RuleFor(c => c.Code).NotEmpty().MaximumLength(32).When(c => c.IsRecoveryCode);
    }
}

public sealed class CompleteTwoFactorLoginCommandHandler : IRequestHandler<CompleteTwoFactorLoginCommand, Result<AuthResult>>
{
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<RecoveryCode<Guid>, Guid> _recoveryCodes;
    private readonly IRecoveryCodeService _recoveryCodeService;
    private readonly TwoFactorCodeVerifier _verifier;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IUserSessionService _sessions;
    private readonly ISecurityEvents _events;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public CompleteTwoFactorLoginCommandHandler(
        IRepository<AppUser, Guid> users,
        IRepository<RecoveryCode<Guid>, Guid> recoveryCodes,
        IRecoveryCodeService recoveryCodeService,
        TwoFactorCodeVerifier verifier,
        AuthTokenIssuer tokenIssuer,
        IUserSessionService sessions,
        ISecurityEvents events,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _users = users;
        _recoveryCodes = recoveryCodes;
        _recoveryCodeService = recoveryCodeService;
        _verifier = verifier;
        _tokenIssuer = tokenIssuer;
        _sessions = sessions;
        _events = events;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<AuthResult>> Handle(CompleteTwoFactorLoginCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> resolved = await TwoFactorChallenges.ResolveUserAsync(_users, request.Challenge, cancellationToken);
        if (resolved.IsFailure)
            return resolved.Errors;

        AppUser user = resolved.Value;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (user.IsLockedOut(now))
            return AuthErrors.LockedOut;

        bool valid = request.IsRecoveryCode
            ? await UseRecoveryCodeAsync(user, request.Code, now, cancellationToken)
            : await _verifier.VerifyAsync(user, request.Code, cancellationToken);

        if (!valid)
        {
            // Kaba kuvvete karşı: hatalı kodlar şifre hataları gibi sayılır ve hesabı kilitler.
            user.RegisterFailedAccess(now);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await AuthEvents.FailedAsync(_events, user, now, request.IsRecoveryCode ? "invalid_recovery_code" : "invalid_code", cancellationToken, SecurityEventTypes.TwoFactorFailed);
            return user.IsLockedOut(now) ? AuthErrors.LockedOut : AuthErrors.InvalidTwoFactorCode;
        }

        user.ResetAccessFailed();
        await _events.PublishAsync(
            new SecurityEvent(request.IsRecoveryCode ? SecurityEventTypes.RecoveryCodeUsed : SecurityEventTypes.TwoFactorSucceeded, user.Id.ToString()),
            cancellationToken
        );

        AuthResult result = await AuthEvents.SignInAsync(user, request.Client, "two_factor", _tokenIssuer, _sessions, _events, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<bool> UseRecoveryCodeAsync(AppUser user, string code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        string hash = _recoveryCodeService.Hash(code);
        RecoveryCode<Guid>? recovery = await _recoveryCodes.GetAsync(r => r.UserId == user.Id && r.CodeHash == hash && r.UsedAt == null, cancellationToken: cancellationToken);
        if (recovery is null)
            return false;

        recovery.MarkUsed(now);
        return true;
    }
}

/// <summary>E-posta ile ikinci adımda yeni kod gönderir.</summary>
public sealed record ResendTwoFactorCodeCommand(TwoFactorChallenge Challenge) : IRequest<Result<Success>>, ITransactionalRequest;

public sealed class ResendTwoFactorCodeCommandHandler : IRequestHandler<ResendTwoFactorCodeCommand, Result<Success>>
{
    private readonly IRepository<AppUser, Guid> _users;
    private readonly EmailVerification _emailVerification;

    public ResendTwoFactorCodeCommandHandler(IRepository<AppUser, Guid> users, EmailVerification emailVerification)
    {
        _users = users;
        _emailVerification = emailVerification;
    }

    public async Task<Result<Success>> Handle(ResendTwoFactorCodeCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> user = await TwoFactorChallenges.ResolveUserAsync(_users, request.Challenge, cancellationToken);
        if (user.IsFailure)
            return user.Errors;

        if (user.Value.AuthenticatorType != AuthenticatorType.Email)
            return Error.Failure("two_factor_not_email", "Bu hesap kodları authenticator uygulamasından alıyor.");

        await _emailVerification.SendCodeAsync(user.Value, VerificationPurpose.SignIn, cancellationToken);
        return Result.Success;
    }
}

internal static class TwoFactorChallenges
{
    /// <summary>Bekleyen girişin kullanıcısını yükler; şifre/2FA ayarı o arada değiştiyse geçersiz sayar.</summary>
    public static async Task<Result<AppUser>> ResolveUserAsync(IRepository<AppUser, Guid> users, TwoFactorChallenge challenge, CancellationToken cancellationToken)
    {
        AppUser? user = await users.GetByIdAsync(challenge.UserId, include: q => q.WithRolesAndPermissions(), cancellationToken: cancellationToken);

        if (user is null || user.SecurityStamp != challenge.SecurityStamp || user.AuthenticatorType != challenge.Method)
            return AuthErrors.TwoFactorExpired;

        return user;
    }
}

// ---------------------------------------------------------------- kod doğrulayıcı

/// <summary>Kullanıcının seçtiği yönteme göre (authenticator uygulaması / e-posta) ikinci adım kodunu doğrular.</summary>
public sealed class TwoFactorCodeVerifier
{
    private readonly IRepository<OtpAuthenticator<Guid>, Guid> _otpAuthenticators;
    private readonly ITotpService _totp;
    private readonly EmailVerification _emailVerification;

    public TwoFactorCodeVerifier(IRepository<OtpAuthenticator<Guid>, Guid> otpAuthenticators, ITotpService totp, EmailVerification emailVerification)
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
    public async Task<OtpAuthenticator<Guid>?> VerifyOtpAsync(Guid userId, string code, bool requireVerified, CancellationToken cancellationToken)
    {
        OtpAuthenticator<Guid>? authenticator = await _otpAuthenticators.GetAsync(a => a.UserId == userId, cancellationToken: cancellationToken);
        if (authenticator is null || (requireVerified && !authenticator.IsVerified))
            return null;

        if (!_totp.TryVerify(authenticator.SecretKey, code, authenticator.LastUsedTimeStep, out long timeStep))
            return null;

        authenticator.MarkCodeUsed(timeStep);
        return authenticator;
    }
}
