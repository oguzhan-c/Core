using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Otp;
using Can.Core.Security.Passkeys;
using Can.Core.Security.RecoveryCodes;
using Fido2NetLib;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Services.AuthService;

public sealed record PasskeyDto(IdentityKey Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, bool IsBackedUp)
{
    public static PasskeyDto From(UserPasskey<IdentityKey> passkey) => new(passkey.Id, passkey.Name, passkey.CreatedAt, passkey.LastUsedAt, passkey.IsBackedUp);
}

/// <summary>Hesaba bağlı dış hesap (Google, Microsoft, GitHub ...).</summary>
public sealed record ExternalLoginDto(string Provider, string? DisplayName, DateTimeOffset CreatedAt)
{
    public static ExternalLoginDto From(UserLogin<IdentityKey> login) => new(login.LoginProvider, login.ProviderDisplayName, login.CreatedAt);
}

/// <summary>Hesap güvenliği sayfasının özeti.</summary>
/// <param name="HasPassword">Şifresi var mı (dış sağlayıcıyla açılan hesapların şifresi olmayabilir).</param>
/// <param name="TwoFactor">Girişte şifreden sonra istenen ikinci adım (<c>None</c>, <c>Email</c>, <c>Otp</c>).</param>
/// <param name="RecoveryCodesLeft">Kullanılmamış kurtarma kodu sayısı (azalınca yenilemesi önerilir).</param>
/// <param name="PasskeysEnabled">Sunucuda passkey ayarlı mı (<c>Security:Passkey</c>).</param>
public sealed record AccountSecurityDto(
    string Email,
    bool EmailConfirmed,
    bool HasPassword,
    AuthenticatorType TwoFactor,
    int RecoveryCodesLeft,
    bool PasskeysEnabled,
    IReadOnlyList<PasskeyDto> Passkeys,
    IReadOnlyList<ExternalLoginDto> ExternalLogins);

/// <summary>Authenticator uygulamasına eklenecek bilgiler: QR kodu (<see cref="ProvisioningUri"/>) ya da elle girilecek anahtar.</summary>
public sealed record OtpSetupDto(string Secret, string ProvisioningUri);

/// <summary>Kurtarma kodları: yalnızca bir kez gösterilir, sunucuda HMAC'leri saklanır.</summary>
public sealed record RecoveryCodesDto(IReadOnlyList<string> Codes);

public static class PasskeyLimits
{
    public const int MaxPerUser = 10;
    public const int NameMaxLength = 64;
}
