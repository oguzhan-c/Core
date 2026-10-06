using Can.Core.MultiTenancy;
using Can.Core.Security.Entities;
using Can.Core.Security.Tokens;
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
    IReadOnlyList<string> Roles)
{
    public static UserProfileDto From(AppUser user, TenantInfo? tenant) =>
        new(user.Id, user.Email, user.FirstName, user.LastName, user.EmailConfirmed, user.TenantId, tenant?.Identifier, tenant?.Name, RolesOf(user));

    /// <remarks><c>UserRoles.Role</c> yüklenmiş olmalı.</remarks>
    public static string[] RolesOf(AppUser user) =>
        user.UserRoles.Where(r => r.Role is not null).Select(r => r.Role!.Name).Order(StringComparer.Ordinal).ToArray();
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
