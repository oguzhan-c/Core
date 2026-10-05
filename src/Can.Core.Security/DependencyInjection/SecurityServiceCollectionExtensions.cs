using Can.Core.Security.Hashing;
using Can.Core.Security.Otp;
using Can.Core.Security.Passkeys;
using Can.Core.Security.Tokens;
using Can.Core.Security.VerificationCodes;
using Fido2NetLib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Security.DependencyInjection;

/// <summary><c>AddCanSecurity(...)</c> ayarları.</summary>
public sealed class CanSecurityOptions
{
    public JwtOptions Jwt { get; set; } = new();

    /// <summary>PBKDF2 iterasyon sayısı. Artırırsan eski hash'ler girişte otomatik yenilenmeli.</summary>
    public int PasswordHashIterations { get; set; } = Pbkdf2PasswordHasher.DefaultIterations;

    /// <summary>
    /// E-posta/SMS doğrulama kodlarını HMAC'lemek için gizli anahtar (en az 32 karakter, JWT anahtarından farklı).
    /// Verilmezse <see cref="IVerificationCodeService"/> kaydedilmez.
    /// </summary>
    public string? VerificationCodeKey { get; set; }

    /// <summary>Passkey ayarları. Verilmezse <see cref="IPasskeyService"/> kaydedilmez.</summary>
    public PasskeyOptions? Passkey { get; set; }
}

public static class SecurityServiceCollectionExtensions
{
    /// <summary>
    /// Şifre hash'leme, JWT/refresh token, TOTP ve (ayarlanmışsa) doğrulama kodu ile passkey servislerini kaydeder.
    /// Ayarlar hatalıysa (ör. kısa imza anahtarı) uygulama açılırken hata verir.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanSecurity(o =&gt;
    /// {
    ///     builder.Configuration.GetSection("Security:Jwt").Bind(o.Jwt);
    ///     o.VerificationCodeKey = builder.Configuration["Security:VerificationCodeKey"];
    ///     o.Passkey = new PasskeyOptions { ServerDomain = "example.com", ServerName = "Can App", Origins = ["https://example.com"] };
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCanSecurity(this IServiceCollection services, Action<CanSecurityOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new CanSecurityOptions();
        configure(options);
        options.Jwt.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(options.Jwt);
        services.TryAddSingleton(TimeProvider.System);

        services.TryAddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(options.PasswordHashIterations));
        services.TryAddSingleton<ITokenService, TokenService>();
        services.TryAddSingleton<ITotpService, TotpService>();

        if (options.VerificationCodeKey is { } codeKey)
        {
            services.TryAddSingleton<IVerificationCodeService>(sp =>
                new VerificationCodeService(codeKey, sp.GetRequiredService<TimeProvider>()));
        }

        if (options.Passkey is { } passkey)
        {
            if (string.IsNullOrWhiteSpace(passkey.ServerDomain) || passkey.Origins.Count == 0)
                throw new InvalidOperationException("Passkey için ServerDomain ve en az bir Origin verilmeli.");

            services.TryAddSingleton<IFido2>(_ =>
                new Fido2(
                    new Fido2Configuration
                    {
                        RPID = passkey.ServerDomain,
                        RPName = string.IsNullOrWhiteSpace(passkey.ServerName) ? passkey.ServerDomain : passkey.ServerName,
                        Origins = new HashSet<string>(passkey.Origins, StringComparer.OrdinalIgnoreCase),
                    }
                )
            );
            services.TryAddSingleton<IPasskeyService, PasskeyService>();
        }

        return services;
    }
}
