namespace CanAppTemplate.Application.Features.Auth.Constants;

/// <summary>
/// Kimlik doğrulama hata metinleri ve kodları. Kod istemciyle sözleşmedir (ProblemDetails <c>code</c>); metinlerin
/// çevirisi WebAPI/Localization/*.json'da koda göre yapılır.
/// </summary>
public static class AuthBusinessMessages
{
    public const string Unauthorized = "Bu işlem için giriş yapmalısın.";
    public const string UnauthorizedCode = "unauthorized";

    public const string InvalidCredentials = "E-posta ya da şifre hatalı.";
    public const string InvalidCredentialsCode = "invalid_credentials";

    public const string LockedOut = "Çok fazla hatalı deneme yapıldı; hesap geçici olarak kilitlendi.";
    public const string LockedOutCode = "locked_out";

    public const string EmailNotConfirmed = "E-posta adresin henüz doğrulanmadı. Gönderdiğimiz kodu gir.";
    public const string EmailNotConfirmedCode = "email_not_confirmed";

    public const string EmailAlreadyConfirmed = "E-posta adresin zaten doğrulanmış; giriş yapabilirsin.";
    public const string EmailAlreadyConfirmedCode = "email_already_confirmed";

    public const string EmailTaken = "Bu e-posta adresiyle kayıtlı bir hesap var.";
    public const string EmailTakenCode = "email_taken";

    public const string InvalidVerificationCode = "Kod hatalı ya da süresi dolmuş; yeni kod iste.";
    public const string InvalidVerificationCodeCode = "invalid_verification_code";

    public const string InvalidTwoFactorCode = "Kod hatalı ya da süresi dolmuş.";
    public const string InvalidTwoFactorCodeCode = "invalid_two_factor_code";

    public const string TwoFactorExpired = "Doğrulama süresi doldu; tekrar giriş yap.";
    public const string TwoFactorExpiredCode = "two_factor_expired";

    public const string TwoFactorNotEmail = "Bu hesap kodları authenticator uygulamasından alıyor.";
    public const string TwoFactorNotEmailCode = "two_factor_not_email";

    public const string PasskeyFailed = "Passkey doğrulanamadı.";
    public const string PasskeyExpired = "Passkey isteğinin süresi doldu; tekrar dene.";
    public const string PasskeyInvalidResponse = "Geçersiz passkey yanıtı.";
    public const string PasskeyFailedCode = "passkey_failed";

    public const string SessionExpired = "Oturumun süresi doldu; tekrar giriş yap.";
    public const string SessionExpiredCode = "session_expired";

    public const string InvalidPassword = "Şifre hatalı.";
    public const string InvalidPasswordCode = "invalid_password";

    public const string ExternalEmailNotVerified =
        "Sağlayıcı doğrulanmış bir e-posta vermedi. Şifrenle giriş yapıp hesabı Hesap güvenliği sayfasından bağlayabilirsin.";
    public const string ExternalEmailNotVerifiedCode = "external_email_not_verified";

    public const string ExternalAlreadyLinked = "Bu dış hesap başka bir kullanıcıya bağlı.";
    public const string ExternalAlreadyLinkedCode = "external_already_linked";

    public const string ExternalProviderLinked = "Bu sağlayıcıda zaten bağlı bir hesabın var; önce onu kaldır.";
    public const string ExternalProviderLinkedCode = "external_provider_linked";
}
