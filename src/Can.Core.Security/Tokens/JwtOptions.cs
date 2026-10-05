namespace Can.Core.Security.Tokens;

/// <summary>JWT ve refresh token ayarları (genelde appsettings'ten okunur).</summary>
public sealed class JwtOptions
{
    public string Issuer { get; set; } = string.Empty;

    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// HMAC-SHA256 imza anahtarı, en az 32 karakter. Koda/appsettings'e yazma; ortam değişkeni ya da
    /// secret store'dan (User Secrets, Key Vault ...) ver.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Saat farkı toleransı.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Issuer))
            throw new InvalidOperationException("JwtOptions.Issuer boş olamaz.");

        if (string.IsNullOrWhiteSpace(Audience))
            throw new InvalidOperationException("JwtOptions.Audience boş olamaz.");

        if (System.Text.Encoding.UTF8.GetByteCount(SigningKey) < 32)
            throw new InvalidOperationException("JwtOptions.SigningKey en az 32 bayt (karakter) olmalı.");
    }
}
