using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Can.Core.Security.Otp;

public interface ITotpService
{
    /// <summary>Kullanıcıya özel yeni bir gizli anahtar (160 bit).</summary>
    byte[] GenerateSecret();

    /// <summary>
    /// Authenticator uygulamasının QR koduna konacak adres:
    /// <c>otpauth://totp/{issuer}:{account}?secret=...&amp;issuer=...</c>.
    /// </summary>
    string GetProvisioningUri(byte[] secret, string issuer, string accountName);

    /// <summary>
    /// Kodu doğrular. Başarılıysa kodun ait olduğu zaman adımını döndürür; aynı kodun tekrar
    /// kullanılmasını engellemek için bunu <c>OtpAuthenticator.LastUsedTimeStep</c> olarak sakla ve
    /// <paramref name="lastUsedTimeStep"/> olarak geri ver.
    /// </summary>
    bool TryVerify(byte[] secret, string code, long? lastUsedTimeStep, out long matchedTimeStep);
}

/// <summary>
/// RFC 6238 TOTP (Google Authenticator, Microsoft Authenticator, 1Password ...): HMAC-SHA1, 6 hane, 30 saniye.
/// Saat farkı için bir önceki ve bir sonraki adım da kabul edilir. Dış paket kullanılmaz.
/// </summary>
public sealed class TotpService : ITotpService
{
    private const int Digits = 6;
    private const int StepSeconds = 30;
    private const int AllowedDrift = 1;

    private readonly TimeProvider _timeProvider;

    public TotpService(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public byte[] GenerateSecret() => RandomNumberGenerator.GetBytes(20);

    public string GetProvisioningUri(byte[] secret, string issuer, string accountName)
    {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);

        string label = Uri.EscapeDataString($"{issuer}:{accountName}");
        return $"otpauth://totp/{label}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}"
            + $"&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    public bool TryVerify(byte[] secret, string code, long? lastUsedTimeStep, out long matchedTimeStep)
    {
        ArgumentNullException.ThrowIfNull(secret);
        matchedTimeStep = 0;

        if (string.IsNullOrWhiteSpace(code))
            return false;

        code = code.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (code.Length != Digits || !code.All(char.IsAsciiDigit))
            return false;

        long currentStep = _timeProvider.GetUtcNow().ToUnixTimeSeconds() / StepSeconds;

        for (long step = currentStep - AllowedDrift; step <= currentStep + AllowedDrift; step++)
        {
            // Daha önce kullanılmış (ya da daha eski) bir adımın kodu tekrar kabul edilmez.
            if (lastUsedTimeStep is { } last && step <= last)
                continue;

            string expected = ComputeCode(secret, step, Digits);
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(expected),
                    System.Text.Encoding.ASCII.GetBytes(code)))
            {
                matchedTimeStep = step;
                return true;
            }
        }

        return false;
    }

    /// <summary>RFC 4226 HOTP.</summary>
    internal static string ComputeCode(byte[] secret, long counter, int digits)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        // TOTP standardı (RFC 6238) ve tüm authenticator uygulamaları HMAC-SHA1 kullanır;
        // HMAC içinde SHA1'in çakışma zayıflığı güvenliği etkilemez.
#pragma warning disable CA5350
        byte[] hash = HMACSHA1.HashData(secret, counterBytes);
#pragma warning restore CA5350

        int offset = hash[^1] & 0x0F;
        int binary =
            ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];

        int modulo = (int)Math.Pow(10, digits);
        return (binary % modulo).ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }
}

/// <summary>RFC 4648 Base32 (authenticator uygulamalarının beklediği biçim).</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var result = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bits = 0;

        foreach (byte b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                result.Append(Alphabet[(buffer >> (bits - 5)) & 0x1F]);
                bits -= 5;
            }
        }

        if (bits > 0)
            result.Append(Alphabet[(buffer << (5 - bits)) & 0x1F]);

        return result.ToString();
    }

    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string clean = text.TrimEnd('=').Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var result = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0;
        int bits = 0;

        foreach (char c in clean)
        {
            int value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
                throw new FormatException($"Geçersiz Base32 karakteri: '{c}'.");

            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits >= 8)
            {
                result.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return result.ToArray();
    }
}
