using Can.Core.Domain.Results;
using Can.Core.Security.Entities;
using Can.Core.Security.Tokens;
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Constants;

/// <summary>İstemcinin ayırt edebilmesi için hata kodları (ProblemDetails <c>code</c> alanı).</summary>
public static class AuthErrorCodes
{
    public const string EmailNotConfirmed = "email_not_confirmed";
    public const string InvalidVerificationCode = "invalid_verification_code";
    public const string InvalidTwoFactorCode = "invalid_two_factor_code";
    public const string TwoFactorExpired = "two_factor_expired";
    public const string PasskeyFailed = "passkey_failed";
    public const string ExternalEmailNotVerified = "external_email_not_verified";
    public const string ExternalAlreadyLinked = "external_already_linked";
    public const string ExternalProviderLinked = "external_provider_linked";
    public const string LastSignInMethod = "last_sign_in_method";
}

/// <summary>Kimlik doğrulama hataları. Kodlar istemciyle sözleşmedir (ProblemDetails <c>code</c>).</summary>
public static class AuthErrors
{
    public static readonly Error InvalidCredentials = Error.Unauthorized("invalid_credentials", "E-posta ya da şifre hatalı.");

    public static readonly Error LockedOut =
        Error.Unauthorized("locked_out", "Çok fazla hatalı deneme yapıldı; hesap geçici olarak kilitlendi.");

    public static readonly Error EmailNotConfirmed =
        Error.Forbidden(AuthErrorCodes.EmailNotConfirmed, "E-posta adresin henüz doğrulanmadı. Gönderdiğimiz kodu gir.");

    public static readonly Error InvalidVerificationCode =
        Error.Failure(AuthErrorCodes.InvalidVerificationCode, "Kod hatalı ya da süresi dolmuş; yeni kod iste.");

    public static readonly Error InvalidTwoFactorCode = Error.Failure(AuthErrorCodes.InvalidTwoFactorCode, "Kod hatalı ya da süresi dolmuş.");

    public static readonly Error TwoFactorExpired = Error.Unauthorized(AuthErrorCodes.TwoFactorExpired, "Doğrulama süresi doldu; tekrar giriş yap.");

    public static readonly Error PasskeyFailed = Error.Unauthorized(AuthErrorCodes.PasskeyFailed, "Passkey doğrulanamadı.");

    public static readonly Error SessionExpired = Error.Unauthorized("session_expired", "Oturumun süresi doldu; tekrar giriş yap.");

    public static readonly Error EmailTaken = Error.Conflict("email_taken", "Bu e-posta adresiyle kayıtlı bir hesap var.");

    public static readonly Error InvalidPassword = Error.Validation("invalid_password", "Şifre hatalı.", "Password");

    public static readonly Error ExternalEmailNotVerified = Error.Forbidden(
        AuthErrorCodes.ExternalEmailNotVerified,
        "Sağlayıcı doğrulanmış bir e-posta vermedi. Şifrenle giriş yapıp hesabı Hesap güvenliği sayfasından bağlayabilirsin."
    );

    public static readonly Error ExternalAlreadyLinked = Error.Conflict(AuthErrorCodes.ExternalAlreadyLinked, "Bu dış hesap başka bir kullanıcıya bağlı.");

    public static readonly Error ExternalProviderLinked =
        Error.Conflict(AuthErrorCodes.ExternalProviderLinked, "Bu sağlayıcıda zaten bağlı bir hesabın var; önce onu kaldır.");

    public static readonly Error ExternalLoginNotFound = Error.NotFound("external_login.not_found", "Bağlı dış hesap bulunamadı.");

    public static readonly Error LastSignInMethod = Error.Conflict(
        AuthErrorCodes.LastSignInMethod,
        "Bu, hesabına girmenin tek yolu. Kaldırmadan önce bir şifre, passkey ya da başka bir dış hesap ekle."
    );
}
