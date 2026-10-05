using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Can.Core.Security.VerificationCodes;

/// <summary>Kullanıcıya gönderilecek kod ve veritabanında saklanacak hash'i.</summary>
public sealed record VerificationCode(string Code, string CodeHash, DateTimeOffset ExpiresAt);

public interface IVerificationCodeService
{
    /// <summary>E-posta/SMS ile gönderilecek tek kullanımlık sayısal kod üretir.</summary>
    VerificationCode Generate(TimeSpan? lifetime = null);

    /// <summary>Kodu, saklanan hash ile sabit sürede karşılaştırır.</summary>
    bool Verify(string code, string codeHash);
}

/// <summary>
/// 6 haneli tek kullanımlık kodlar (e-posta ile giriş doğrulama, e-posta onayı, şifre sıfırlama ...).
/// </summary>
/// <remarks>
/// Kod düz metin değil, gizli bir anahtarla HMAC'lenmiş hâliyle saklanır; veritabanı sızsa bile 1.000.000
/// olasılık anahtar bilinmeden denenemez. Kaba kuvvete karşı ayrıca kısa ömür ve deneme sınırı
/// (<c>EmailAuthenticator.FailedAttempts</c>) uygulanmalıdır.
/// </remarks>
public sealed class VerificationCodeService : IVerificationCodeService
{
    private const int Digits = 6;

    private readonly byte[] _key;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _defaultLifetime;

    public VerificationCodeService(string secretKey, TimeProvider timeProvider, TimeSpan? defaultLifetime = null)
    {
        if (Encoding.UTF8.GetByteCount(secretKey ?? string.Empty) < 32)
            throw new InvalidOperationException("Doğrulama kodu anahtarı en az 32 bayt olmalı.");

        _key = Encoding.UTF8.GetBytes(secretKey!);
        _timeProvider = timeProvider;
        _defaultLifetime = defaultLifetime ?? TimeSpan.FromMinutes(10);
    }

    public VerificationCode Generate(TimeSpan? lifetime = null)
    {
        string code = RandomNumberGenerator
            .GetInt32(0, (int)Math.Pow(10, Digits))
            .ToString(CultureInfo.InvariantCulture)
            .PadLeft(Digits, '0');

        return new VerificationCode(code, Hash(code), _timeProvider.GetUtcNow().Add(lifetime ?? _defaultLifetime));
    }

    public bool Verify(string code, string codeHash)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrEmpty(codeHash))
            return false;

        byte[] expected;
        try
        {
            expected = Convert.FromHexString(codeHash);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(HashBytes(code.Trim()), expected);
    }

    private string Hash(string code) => Convert.ToHexString(HashBytes(code));

    private byte[] HashBytes(string code) => HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(code));
}
