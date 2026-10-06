using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Otp;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Common;
using Northwind.Domain.Identity;

namespace Northwind.Application.Features.Auth;

// ---------------------------------------------------------------- ikinci adım

/// <summary>
/// İki adımlı girişin ikinci adımı: authenticator uygulamasındaki ya da e-postaya gelen 6 haneli kod.
/// <see cref="Challenge"/> sunucunun şifreleyip cookie'de sakladığı bilgidir; istemci değiştiremez.
/// </summary>
public sealed record CompleteTwoFactorLoginCommand(TwoFactorChallenge Challenge, string Code, string? IpAddress = null)
    : IRequest<Result<AuthResult>>, ILoggableRequest;

public sealed class CompleteTwoFactorLoginCommandValidator : AbstractValidator<CompleteTwoFactorLoginCommand>
{
    public CompleteTwoFactorLoginCommandValidator()
    {
        RuleFor(c => c.Code).NotEmpty().Matches("^[0-9]{6}$").WithMessage("Kod 6 haneli olmalı.");
    }
}

public sealed class CompleteTwoFactorLoginCommandHandler : IRequestHandler<CompleteTwoFactorLoginCommand, Result<AuthResult>>
{
    private readonly StoreTenant _storeTenant;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly TwoFactorCodeVerifier _verifier;
    private readonly AuthTokenIssuer _tokenIssuer;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public CompleteTwoFactorLoginCommandHandler(
        StoreTenant storeTenant,
        IRepository<AppUser, Guid> users,
        TwoFactorCodeVerifier verifier,
        AuthTokenIssuer tokenIssuer,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        _storeTenant = storeTenant;
        _users = users;
        _verifier = verifier;
        _tokenIssuer = tokenIssuer;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<AuthResult>> Handle(CompleteTwoFactorLoginCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> resolved = await TwoFactorChallenges.ResolveUserAsync(_storeTenant, _users, request.Challenge, cancellationToken);
        if (resolved.IsFailure)
            return resolved.Errors;

        AppUser user = resolved.Value;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (user.IsLockedOut(now))
            return AuthErrors.LockedOut;

        if (!await _verifier.VerifyAsync(user, request.Code, cancellationToken))
        {
            // Kaba kuvvete karşı: hatalı kodlar şifre hataları gibi sayılır ve hesabı kilitler.
            user.RegisterFailedAccess(now);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            return user.IsLockedOut(now) ? AuthErrors.LockedOut : AuthErrors.InvalidTwoFactorCode;
        }

        user.ResetAccessFailed();
        AuthResult result = await _tokenIssuer.IssueAsync(user, request.IpAddress, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return result;
    }
}

/// <summary>E-posta ile ikinci adımda yeni kod gönderir.</summary>
public sealed record ResendTwoFactorCodeCommand(TwoFactorChallenge Challenge) : IRequest<Result<Success>>, ITransactionalRequest;

public sealed class ResendTwoFactorCodeCommandHandler : IRequestHandler<ResendTwoFactorCodeCommand, Result<Success>>
{
    private readonly StoreTenant _storeTenant;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly EmailVerification _emailVerification;

    public ResendTwoFactorCodeCommandHandler(StoreTenant storeTenant, IRepository<AppUser, Guid> users, EmailVerification emailVerification)
    {
        _storeTenant = storeTenant;
        _users = users;
        _emailVerification = emailVerification;
    }

    public async Task<Result<Success>> Handle(ResendTwoFactorCodeCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> user = await TwoFactorChallenges.ResolveUserAsync(_storeTenant, _users, request.Challenge, cancellationToken);
        if (user.IsFailure)
            return user.Errors;

        if (user.Value.AuthenticatorType != AuthenticatorType.Email)
            return Error.Failure("two_factor_not_email", "Bu hesap kodları authenticator uygulamasından alıyor.");

        await _emailVerification.SendCodeAsync(user.Value, signIn: true, cancellationToken);
        return Result.Success;
    }
}

internal static class TwoFactorChallenges
{
    /// <summary>Bekleyen girişin kullanıcısını yükler; şifre/2FA ayarı o arada değiştiyse geçersiz sayar.</summary>
    public static async Task<Result<AppUser>> ResolveUserAsync(
        StoreTenant storeTenant,
        IRepository<AppUser, Guid> users,
        TwoFactorChallenge challenge,
        CancellationToken cancellationToken)
    {
        if ((await storeTenant.UseAsync(challenge.Tenant, cancellationToken)).IsFailure)
            return AuthErrors.TwoFactorExpired;

        AppUser? user = await users.GetByIdAsync(
            challenge.UserId,
            include: q => q.WithRolesAndPermissions(),
            cancellationToken: cancellationToken
        );

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
