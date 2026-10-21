namespace CanAppTemplate.Application.Features.Account.Constants;

/// <summary>Hesap güvenliği hata metinleri ve kodları (ProblemDetails <c>code</c>; çeviriler WebAPI/Localization/*.json).</summary>
public static class AccountBusinessMessages
{
    public const string CurrentPasswordInvalid = "Mevcut şifre hatalı.";
    public const string CurrentPasswordInvalidCode = "invalid_password";

    public const string OtpAlreadyEnabled = "Authenticator uygulaması zaten açık. Yenisini kurmak için önce iki adımlı doğrulamayı kapat.";
    public const string OtpAlreadyEnabledCode = "otp.already_enabled";

    public const string OtpCodeInvalid = "Kod hatalı. Uygulamadaki güncel kodu gir (saatin doğru olduğundan emin ol).";
    public const string OtpCodeInvalidCode = "invalid_two_factor_code";

    public const string EmailNotConfirmed = "Önce e-posta adresini doğrula.";
    public const string EmailNotConfirmedCode = "email_not_confirmed";

    public const string TwoFactorDisabled = "Kurtarma kodları yalnızca iki adımlı doğrulama açıkken kullanılır.";
    public const string TwoFactorDisabledCode = "two_factor_disabled";

    public const string PasskeyNotFound = "Passkey bulunamadı.";
    public const string PasskeyNotFoundCode = "passkey.not_found";

    public const string PasskeyWrongUser = "Passkey isteği bu hesaba ait değil.";
    public const string PasskeyWrongUserCode = "passkey.wrong_user";

    /// <summary>{0}: en fazla passkey sayısı.</summary>
    public const string PasskeyLimit = "En fazla {0} passkey eklenebilir; önce birini sil.";
    public const string PasskeyLimitCode = "passkey.limit";

    public const string PasskeyRegistrationFailed = "Passkey kaydedilemedi; tekrar dene.";
    public const string PasskeyRegistrationExpired = "Passkey isteğinin süresi doldu; tekrar dene.";
    public const string PasskeyFailedCode = "passkey_failed";

    public const string ExternalLoginNotFound = "Bağlı dış hesap bulunamadı.";
    public const string ExternalLoginNotFoundCode = "external_login.not_found";

    public const string LastSignInMethod = "Bu, hesabına girmenin tek yolu. Kaldırmadan önce bir şifre, passkey ya da başka bir dış hesap ekle.";
    public const string LastSignInMethodCode = "last_sign_in_method";
}
