using System.Security.Cryptography;
using System.Text;

namespace Can.Core.Security.Hashing;

public enum PasswordVerificationResult
{
    Failed,
    Success,

    /// <summary>Şifre doğru ama hash eski ayarlarla üretilmiş; giriş sırasında yeniden hash'leyip kaydet.</summary>
    SuccessRehashNeeded,
}

public interface IPasswordHasher
{
    string Hash(string password);

    PasswordVerificationResult Verify(string password, string passwordHash);
}

/// <summary>
/// PBKDF2-HMAC-SHA256 ile şifre hash'leme (OWASP önerisi: 600.000 iterasyon).
/// </summary>
/// <remarks>
/// <para>
/// nArchitecture'daki HMACSHA512 tek seferde hesaplanır ve saniyede milyarlarca deneme yapılabilir;
/// PBKDF2 bilinçli olarak yavaştır, böylece sızan bir veritabanındaki şifreler kaba kuvvetle kırılamaz.
/// </para>
/// <para>
/// Çıktı tek bir metindir: <c>v1.{iterasyon}.{salt}.{hash}</c> (Base64). Salt ve ayarlar hash'in içinde
/// saklandığı için ayrı bir PasswordSalt kolonuna gerek yoktur; iterasyon sayısı ileride artırılırsa eski
/// hash'ler <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> ile işaretlenir.
/// </para>
/// </remarks>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Version = "v1";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    public const int DefaultIterations = 600_000;

    private readonly int _iterations;

    public Pbkdf2PasswordHasher(int iterations = DefaultIterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 10_000);
        _iterations = iterations;
    }

    public string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Derive(password, salt, _iterations);

        return $"{Version}.{_iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    public PasswordVerificationResult Verify(string password, string passwordHash)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (string.IsNullOrEmpty(passwordHash))
            return PasswordVerificationResult.Failed;

        string[] parts = passwordHash.Split('.');
        if (parts.Length != 4 || parts[0] != Version || !int.TryParse(parts[1], out int iterations) || iterations <= 0)
            return PasswordVerificationResult.Failed;

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return PasswordVerificationResult.Failed;
        }

        byte[] actual = Derive(password, salt, iterations, expected.Length);

        // Sabit süreli karşılaştırma: zamanlama saldırısıyla hash tahmin edilemez.
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
            return PasswordVerificationResult.Failed;

        return iterations < _iterations
            ? PasswordVerificationResult.SuccessRehashNeeded
            : PasswordVerificationResult.Success;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int length = HashSize) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, length);
}
