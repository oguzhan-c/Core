using Can.Core.Domain.Results;
using Can.Core.Security.Entities;
using Can.Core.Security.Tokens;
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Services.AuthService;

public sealed record UserProfileDto(
    IdentityKey Id,
    string Email,
    string FirstName,
    string LastName,
    string? PhoneNumber,
    bool EmailConfirmed,
    bool MarketingConsent,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions)
{
    /// <remarks>Roller ve yetkiler yüklenmiş olmalı (<see cref="AppUserQueryExtensions.WithRolesAndPermissions"/>).</remarks>
    public static UserProfileDto From(AppUser user) =>
        new(
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            user.PhoneNumber,
            user.EmailConfirmed,
            user.MarketingConsent,
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
public sealed record AuthResult(AccessToken AccessToken, RefreshTokenValue RefreshToken, UserProfileDto User, string SessionId);

/// <summary>
/// Şifre doğrulandı ama ikinci adım gerekiyor. İstemciye gönderilmez: WebApi şifreleyip kısa ömürlü HttpOnly cookie'ye
/// yazar, ikinci adımda geri okur. <see cref="SecurityStamp"/> değiştiyse (şifre/2FA değişti) geçersizdir.
/// </summary>
public sealed record TwoFactorChallenge(IdentityKey UserId, string SecurityStamp, AuthenticatorType Method);

/// <summary>İkinci adım için istemciye gösterilecek bilgi.</summary>
public sealed record TwoFactorPrompt(AuthenticatorType Method, string? Destination);

/// <summary>Şifre ile giriş sonucu: ya oturum açıldı ya da ikinci adım isteniyor.</summary>
public sealed record LoginResult(AuthResult? Session, TwoFactorChallenge? Challenge, TwoFactorPrompt? Prompt)
{
    public static LoginResult SignedIn(AuthResult session) => new(session, null, null);

    public static LoginResult RequiresTwoFactor(TwoFactorChallenge challenge, TwoFactorPrompt prompt) => new(null, challenge, prompt);
}
