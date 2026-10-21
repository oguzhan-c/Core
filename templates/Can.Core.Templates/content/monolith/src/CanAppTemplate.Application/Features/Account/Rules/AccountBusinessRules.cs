using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Can.Core.Application.Exceptions;
using Can.Core.Application.Rules;
using Can.Core.Domain.Exceptions;
using Can.Core.Security.Entities;
using CanAppTemplate.Application.Features.Account.Constants;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Rules;

/// <summary>
/// Hesap güvenliği kuralları (şifre, iki adımlı doğrulama, passkey, dış hesaplar). Giriş yapmış kullanıcıyı getirme ve
/// şifre doğrulama gibi ortak kurallar <c>AuthBusinessRules</c>'tadır.
/// </summary>
public sealed class AccountBusinessRules : BaseBusinessRules
{
    public void CurrentPasswordShouldMatch([DoesNotReturnIf(false)] bool matches)
    {
        if (!matches)
            throw new ValidationException("CurrentPassword", AccountBusinessMessages.CurrentPasswordInvalid) { Code = AccountBusinessMessages.CurrentPasswordInvalidCode };
    }

    public void OtpShouldNotBeEnabled(AppUser user)
    {
        if (user.AuthenticatorType == AuthenticatorType.Otp)
            throw new ConflictException(AccountBusinessMessages.OtpAlreadyEnabled) { Code = AccountBusinessMessages.OtpAlreadyEnabledCode };
    }

    public void OtpCodeShouldBeValid([DoesNotReturnIf(false)] bool valid)
    {
        if (!valid)
            throw new BusinessException(AccountBusinessMessages.OtpCodeInvalid) { Code = AccountBusinessMessages.OtpCodeInvalidCode };
    }

    /// <summary>E-postayla iki adımlı doğrulama için adres doğrulanmış olmalı.</summary>
    public void EmailShouldBeConfirmedForTwoFactor(AppUser user)
    {
        if (!user.EmailConfirmed)
            throw new BusinessException(AccountBusinessMessages.EmailNotConfirmed) { Code = AccountBusinessMessages.EmailNotConfirmedCode };
    }

    public void TwoFactorShouldBeEnabled(AppUser user)
    {
        if (user.AuthenticatorType == AuthenticatorType.None)
            throw new BusinessException(AccountBusinessMessages.TwoFactorDisabled) { Code = AccountBusinessMessages.TwoFactorDisabledCode };
    }

    public void PasskeyShouldExist([DoesNotReturnIf(false)] bool exists)
    {
        if (!exists)
            throw new NotFoundException(AccountBusinessMessages.PasskeyNotFound) { Code = AccountBusinessMessages.PasskeyNotFoundCode };
    }

    public void PasskeyLimitShouldNotBeExceeded(int existingCount)
    {
        if (existingCount >= PasskeyLimits.MaxPerUser)
        {
            throw new BusinessException(string.Format(CultureInfo.InvariantCulture, AccountBusinessMessages.PasskeyLimit, PasskeyLimits.MaxPerUser))
            {
                Code = AccountBusinessMessages.PasskeyLimitCode,
            };
        }
    }

    /// <summary>Kayıt seçenekleri bu kullanıcı için üretilmiş olmalı (başka oturumun seçenekleriyle kayıt yapılamaz).</summary>
    public void PasskeyOptionsShouldBelongToUser(bool belongs)
    {
        if (!belongs)
            throw new ForbiddenException(AccountBusinessMessages.PasskeyWrongUser) { Code = AccountBusinessMessages.PasskeyWrongUserCode };
    }

    public void PasskeyShouldBeRegistered([DoesNotReturnIf(false)] bool registered)
    {
        if (!registered)
            throw new BusinessException(AccountBusinessMessages.PasskeyRegistrationFailed) { Code = AccountBusinessMessages.PasskeyFailedCode };
    }

    public void PasskeyCeremonyShouldNotBeExpired([DoesNotReturnIf(false)] bool exists)
    {
        if (!exists)
            throw new BusinessException(AccountBusinessMessages.PasskeyRegistrationExpired) { Code = AccountBusinessMessages.PasskeyFailedCode };
    }

    public void ExternalLoginShouldExist(bool exists)
    {
        if (!exists)
            throw new NotFoundException(AccountBusinessMessages.ExternalLoginNotFound) { Code = AccountBusinessMessages.ExternalLoginNotFoundCode };
    }

    /// <summary>Kaldırılan yöntem hesaba girmenin tek yolu olmamalı (şifre, başka passkey ya da dış hesap kalmalı).</summary>
    public void SignInMethodShouldRemain(bool hasOtherWay)
    {
        if (!hasOtherWay)
            throw new ConflictException(AccountBusinessMessages.LastSignInMethod) { Code = AccountBusinessMessages.LastSignInMethodCode };
    }
}
