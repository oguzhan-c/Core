using Can.Core.Domain.Results;
using Can.Core.MultiTenancy;
using Can.Core.Security.Entities;
using Can.Core.Security.Tokens;
using Microsoft.EntityFrameworkCore;
using Northwind.Domain.Identity;

namespace Northwind.Application.Features.Auth;

public sealed record UserProfileDto(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    bool EmailConfirmed,
    Guid TenantId,
    string? Tenant,
    string? TenantName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions)
{
    /// <remarks>Roller ve yetkiler yüklenmiş olmalı (<see cref="AppUserQueryExtensions.WithRolesAndPermissions"/>).</remarks>
    public static UserProfileDto From(AppUser user, TenantInfo? tenant) =>
        new(
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            user.EmailConfirmed,
            user.TenantId,
            tenant?.Identifier,
            tenant?.Name,
            RolesOf(user),
            user.GetPermissionNames()
        );

    /// <remarks><c>UserRoles.Role</c> yüklenmiş olmalı.</remarks>
    public static string[] RolesOf(AppUser user) =>
        user.UserRoles.Where(r => r.Role is not null).Select(r => r.Role!.Name).Order(StringComparer.Ordinal).ToArray();
}

public static class AppUserQueryExtensions
{
    /// <summary>Token ve profil için gereken roller + yetkiler (rollerin ve doğrudan verilenlerin).</summary>
    public static IQueryable<AppUser> WithRolesAndPermissions(this IQueryable<AppUser> query) =>
        query
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role!).ThenInclude(r => r.OperationClaims).ThenInclude(rc => rc.OperationClaim)
            .Include(u => u.OperationClaims).ThenInclude(uc => uc.OperationClaim)
            .AsSplitQuery();
}

/// <summary>
/// Giriş/yenileme sonucu. Token'lar yanıt gövdesine konmaz; WebApi bunları HttpOnly cookie olarak yazar.
/// </summary>
public sealed record AuthResult(AccessToken AccessToken, RefreshTokenValue RefreshToken, UserProfileDto User);

/// <summary>
/// Şifre doğrulandı ama ikinci adım gerekiyor. İstemciye gönderilmez: WebApi bunu şifreleyip kısa ömürlü HttpOnly
/// cookie'ye yazar, ikinci adımda geri okur. <see cref="SecurityStamp"/> değiştiyse (şifre/2FA değişti) geçersizdir.
/// </summary>
public sealed record TwoFactorChallenge(string Tenant, Guid UserId, string SecurityStamp, AuthenticatorType Method);

/// <summary>İkinci adım için istemciye gösterilecek bilgi.</summary>
public sealed record TwoFactorPrompt(AuthenticatorType Method, string? Destination);

/// <summary>Şifre ile giriş sonucu: ya oturum açıldı ya da ikinci adım isteniyor.</summary>
public sealed record LoginResult(AuthResult? Session, TwoFactorChallenge? Challenge, TwoFactorPrompt? Prompt)
{
    public static LoginResult SignedIn(AuthResult session) => new(session, null, null);

    public static LoginResult RequiresTwoFactor(TwoFactorChallenge challenge, TwoFactorPrompt prompt) => new(null, challenge, prompt);
}

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
    public static readonly Error InvalidCredentials =
        Error.Unauthorized("invalid_credentials", "Mağaza, e-posta ya da şifre hatalı.");

    public static readonly Error LockedOut =
        Error.Unauthorized("locked_out", "Çok fazla hatalı deneme yapıldı; hesap geçici olarak kilitlendi.");

    public static readonly Error EmailNotConfirmed =
        Error.Forbidden(AuthErrorCodes.EmailNotConfirmed, "E-posta adresin henüz doğrulanmadı. Gönderdiğimiz kodu gir.");

    public static readonly Error InvalidVerificationCode =
        Error.Failure(AuthErrorCodes.InvalidVerificationCode, "Kod hatalı ya da süresi dolmuş; yeni kod iste.");

    public static readonly Error InvalidTwoFactorCode = Error.Failure(AuthErrorCodes.InvalidTwoFactorCode, "Kod hatalı ya da süresi dolmuş.");

    public static readonly Error TwoFactorExpired = Error.Unauthorized(AuthErrorCodes.TwoFactorExpired, "Doğrulama süresi doldu; tekrar giriş yap.");

    public static readonly Error PasskeyFailed =
        Error.Unauthorized(AuthErrorCodes.PasskeyFailed, "Passkey doğrulanamadı ya da bu mağazadaki bir hesaba ait değil.");

    public static readonly Error SessionExpired = Error.Unauthorized("session_expired", "Oturumun süresi doldu; tekrar giriş yap.");

    public static readonly Error EmailTaken = Error.Conflict("email_taken", "Bu e-posta adresiyle kayıtlı bir hesap var.");

    public static readonly Error ExternalEmailNotVerified = Error.Forbidden(
        AuthErrorCodes.ExternalEmailNotVerified,
        "Sağlayıcı doğrulanmış bir e-posta vermedi. Şifrenle giriş yapıp hesabı Hesap güvenliği sayfasından bağlayabilirsin."
    );

    public static readonly Error ExternalAlreadyLinked =
        Error.Conflict(AuthErrorCodes.ExternalAlreadyLinked, "Bu dış hesap bu mağazada başka bir kullanıcıya bağlı.");

    public static readonly Error ExternalProviderLinked =
        Error.Conflict(AuthErrorCodes.ExternalProviderLinked, "Bu sağlayıcıda zaten bağlı bir hesabın var; önce onu kaldır.");

    public static readonly Error ExternalLoginNotFound = Error.NotFound("external_login.not_found", "Bağlı dış hesap bulunamadı.");

    public static readonly Error LastSignInMethod = Error.Conflict(
        AuthErrorCodes.LastSignInMethod,
        "Bu, hesabına girmenin tek yolu. Kaldırmadan önce bir passkey ya da başka bir dış hesap ekle."
    );
}

internal static class EmailMask
{
    /// <summary><c>mematixan@gmail.com</c> → <c>me*******@gmail.com</c>.</summary>
    public static string Mask(string email)
    {
        int at = email.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
            return email;

        int visible = Math.Min(2, at);
        return string.Concat(email.AsSpan(0, visible), new string('*', Math.Max(1, at - visible)), email.AsSpan(at));
    }
}
