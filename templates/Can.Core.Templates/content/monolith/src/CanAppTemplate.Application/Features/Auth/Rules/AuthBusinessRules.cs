using System.Diagnostics.CodeAnalysis;
using Can.Core.Application;
using Can.Core.Application.Exceptions;
using Can.Core.Application.Rules;
using Can.Core.Domain.Exceptions;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Rules;

/// <summary>
/// Kimlik doğrulama kuralları. Kural tutmazsa exception fırlar (<c>UnauthorizedException</c> → 401, <c>ForbiddenException</c>
/// → 403, <c>ConflictException</c> → 409, <c>BusinessException</c> → 400); WebAPI ProblemDetails'e çevirir, kod
/// <see cref="AuthBusinessMessages"/>'tadır.
/// </summary>
/// <remarks>
/// Kalıcı olması gereken yan etkiler (hatalı deneme sayacı, güvenlik olayı) kural çağrılmadan ÖNCE kaydedilir: exception
/// transaction'lı isteklerde her şeyi geri alır.
/// </remarks>
public sealed class AuthBusinessRules : BaseBusinessRules
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, IdentityKey> _userRepository;

    public AuthBusinessRules(ICurrentUser currentUser, IRepository<AppUser, IdentityKey> userRepository)
    {
        _currentUser = currentUser;
        _userRepository = userRepository;
    }

    /// <summary>Giriş yapmış kullanıcının kimliği; giriş yoksa 401.</summary>
    public IdentityKey CurrentUserIdShouldBeKnown() =>
        IdentityKey.TryParse(_currentUser.Id, out IdentityKey userId) ? userId : throw UnauthorizedError();

    /// <summary>Giriş yapmış kullanıcıyı getirir; yoksa (silinmiş) 401.</summary>
    public async Task<AppUser> CurrentUserShouldExist(
        Func<IQueryable<AppUser>, IQueryable<AppUser>>? include = null,
        bool enableTracking = true,
        CancellationToken cancellationToken = default)
    {
        IdentityKey userId = CurrentUserIdShouldBeKnown();
        return await _userRepository.GetByIdAsync(userId, include: include, enableTracking: enableTracking, cancellationToken: cancellationToken)
            ?? throw UnauthorizedError();
    }

    /// <summary>Kullanıcı bulunmalı (ör. şifreli state'ten gelen kullanıcı); yoksa 401.</summary>
    public void UserShouldExist([DoesNotReturnIf(false)] bool exists)
    {
        if (!exists)
            throw UnauthorizedError();
    }

    /// <summary>Bekleyen girişin kullanıcısını getirir; şifre / 2FA ayarı o arada değiştiyse geçersizdir.</summary>
    public async Task<AppUser> TwoFactorChallengeShouldBeValid(TwoFactorChallenge challenge, CancellationToken cancellationToken = default)
    {
        AppUser? user = await _userRepository.GetByIdAsync(challenge.UserId, include: q => q.WithRolesAndPermissions(), cancellationToken: cancellationToken);
        if (user is null || user.SecurityStamp != challenge.SecurityStamp || user.AuthenticatorType != challenge.Method)
            throw TwoFactorExpiredError();
        return user;
    }

    /// <summary>E-posta (silinmiş hesaplar dahil) kullanılmamış olmalı.</summary>
    public async Task EmailShouldNotBeTaken(string normalizedEmail, CancellationToken cancellationToken = default)
    {
        if (await _userRepository.AnyAsync(u => u.NormalizedEmail == normalizedEmail, withDeleted: true, cancellationToken: cancellationToken))
            throw new ConflictException(AuthBusinessMessages.EmailTaken) { Code = AuthBusinessMessages.EmailTakenCode };
    }

    /// <summary>Şifre doğru olmalı. Hatalı denemeyle hesap kilitlendiyse "kilitlendi", değilse "e-posta ya da şifre hatalı".</summary>
    /// <param name="user">Bulunamadıysa <see langword="null"/>: yanıt, hesabın var olup olmadığını belli etmez.</param>
    public void CredentialsShouldBeValid([DoesNotReturnIf(false)] bool valid, AppUser? user, DateTimeOffset now)
    {
        if (valid)
            return;

        if (user is not null && user.IsLockedOut(now))
            throw LockedOutError();

        throw new UnauthorizedException(AuthBusinessMessages.InvalidCredentials) { Code = AuthBusinessMessages.InvalidCredentialsCode };
    }

    public void UserShouldNotBeLockedOut(AppUser user, DateTimeOffset now)
    {
        if (user.IsLockedOut(now))
            throw LockedOutError();
    }

    public void EmailShouldBeConfirmed(AppUser user) => EmailShouldBeVerifiedByProvider(user.EmailConfirmed);

    /// <summary>Dış sağlayıcı hesabın e-postasını doğrulamış olmalı (aksi hâlde kodla doğrulanır).</summary>
    public void EmailShouldBeVerifiedByProvider(bool verified)
    {
        if (!verified)
            throw new ForbiddenException(AuthBusinessMessages.EmailNotConfirmed) { Code = AuthBusinessMessages.EmailNotConfirmedCode };
    }

    /// <summary>Doğrulanmış hesapta e-posta doğrulama ucu şifresiz giriş kapısı olamaz (2FA atlanırdı).</summary>
    public void EmailShouldNotBeConfirmedYet(AppUser user)
    {
        if (user.EmailConfirmed)
            throw new ConflictException(AuthBusinessMessages.EmailAlreadyConfirmed) { Code = AuthBusinessMessages.EmailAlreadyConfirmedCode };
    }

    public void VerificationCodeShouldBeValid([DoesNotReturnIf(false)] bool valid)
    {
        if (!valid)
            throw new BusinessException(AuthBusinessMessages.InvalidVerificationCode) { Code = AuthBusinessMessages.InvalidVerificationCodeCode };
    }

    /// <summary>İkinci adım kodu doğru olmalı; hatalı denemeyle hesap kilitlendiyse "kilitlendi".</summary>
    public void TwoFactorCodeShouldBeValid(bool valid, AppUser user, DateTimeOffset now)
    {
        if (valid)
            return;

        if (user.IsLockedOut(now))
            throw LockedOutError();

        throw new BusinessException(AuthBusinessMessages.InvalidTwoFactorCode) { Code = AuthBusinessMessages.InvalidTwoFactorCodeCode };
    }

    public void TwoFactorChallengeShouldExist([DoesNotReturnIf(false)] bool exists)
    {
        if (!exists)
            throw TwoFactorExpiredError();
    }

    public void TwoFactorShouldUseEmail(AppUser user)
    {
        if (user.AuthenticatorType != AuthenticatorType.Email)
            throw new BusinessException(AuthBusinessMessages.TwoFactorNotEmail) { Code = AuthBusinessMessages.TwoFactorNotEmailCode };
    }

    public void SessionShouldBeActive([DoesNotReturnIf(false)] bool active)
    {
        if (!active)
            throw new UnauthorizedException(AuthBusinessMessages.SessionExpired) { Code = AuthBusinessMessages.SessionExpiredCode };
    }

    public void PasskeyShouldBeVerified([DoesNotReturnIf(false)] bool verified)
    {
        if (!verified)
            throw new UnauthorizedException(AuthBusinessMessages.PasskeyFailed) { Code = AuthBusinessMessages.PasskeyFailedCode };
    }

    public void PasskeyCeremonyShouldNotBeExpired([DoesNotReturnIf(false)] bool exists)
    {
        if (!exists)
            throw new UnauthorizedException(AuthBusinessMessages.PasskeyExpired) { Code = AuthBusinessMessages.PasskeyFailedCode };
    }

    /// <summary>Şifre doğru olmalı (hassas işlemlerde yeniden doğrulama).</summary>
    public void PasswordShouldMatch([DoesNotReturnIf(false)] bool matches, string field = "Password")
    {
        if (!matches)
            throw new ValidationException(field, AuthBusinessMessages.InvalidPassword) { Code = AuthBusinessMessages.InvalidPasswordCode };
    }

    public void ExternalEmailShouldBeVerified([DoesNotReturnIf(false)] bool verified)
    {
        if (!verified)
            throw new ForbiddenException(AuthBusinessMessages.ExternalEmailNotVerified) { Code = AuthBusinessMessages.ExternalEmailNotVerifiedCode };
    }

    /// <summary>Aynı sağlayıcıda kullanıcının başka bir hesabı bağlı olmamalı (<c>AppUser.AddLogin</c>'in sonucu).</summary>
    public void ExternalLoginShouldBeAdded(bool added)
    {
        if (!added)
            throw new ConflictException(AuthBusinessMessages.ExternalProviderLinked) { Code = AuthBusinessMessages.ExternalProviderLinkedCode };
    }

    /// <summary>Dış hesap başka bir kullanıcıya bağlı olmamalı.</summary>
    public async Task ExternalLoginShouldNotBelongToAnotherUser(IdentityKey userId, string provider, string providerKey, CancellationToken cancellationToken = default)
    {
        if (await _userRepository.AnyAsync(
                u => u.Id != userId && u.Logins.Any(l => l.LoginProvider == provider && l.ProviderKey == providerKey),
                cancellationToken: cancellationToken))
        {
            throw new ConflictException(AuthBusinessMessages.ExternalAlreadyLinked) { Code = AuthBusinessMessages.ExternalAlreadyLinkedCode };
        }
    }

    private static UnauthorizedException UnauthorizedError() =>
        new(AuthBusinessMessages.Unauthorized) { Code = AuthBusinessMessages.UnauthorizedCode };

    private static UnauthorizedException LockedOutError() =>
        new(AuthBusinessMessages.LockedOut) { Code = AuthBusinessMessages.LockedOutCode };

    private static UnauthorizedException TwoFactorExpiredError() =>
        new(AuthBusinessMessages.TwoFactorExpired) { Code = AuthBusinessMessages.TwoFactorExpiredCode };
}
