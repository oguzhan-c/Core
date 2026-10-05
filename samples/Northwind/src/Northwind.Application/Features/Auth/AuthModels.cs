using Can.Core.MultiTenancy;
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

/// <summary>İstemcinin ayırt edebilmesi için hata kodları (ProblemDetails <c>code</c> alanı).</summary>
public static class AuthErrorCodes
{
    public const string EmailNotConfirmed = "email_not_confirmed";
    public const string InvalidVerificationCode = "invalid_verification_code";
}
