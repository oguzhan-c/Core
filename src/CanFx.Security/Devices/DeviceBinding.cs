using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace CanFx.Security.Devices;

/// <summary>Kayıtlı cihazın imzalayacağı tek kullanımlık metin.</summary>
/// <param name="Challenge">İmzalanacak metin (UTF-8 baytları imzalanır).</param>
/// <param name="ExpiresAt">Bu ana kadar geçerli.</param>
public sealed record DeviceChallenge(string Challenge, DateTimeOffset ExpiresAt);

/// <summary>İmza doğrulamasının sonucu.</summary>
/// <param name="IsValid">İmza geçerli ve meydan okuma bu cihaz için, süresi içinde ve daha önce kullanılmamış.</param>
/// <param name="IssuedAt">Meydan okumanın üretildiği an (cihaz kaydına "son kullanılan" olarak yazılır: tekrar kullanılamaz).</param>
/// <param name="Failure">Geçersizse nedeni (log için; istemciye ayrıntı verme).</param>
public sealed record DeviceVerification(bool IsValid, DateTimeOffset IssuedAt, string? Failure)
{
    internal static DeviceVerification Fail(string reason) => new(false, default, reason);
}

/// <summary>
/// Cihaz bağlama (bankacılık uygulamalarındaki "kayıtlı cihaz"): mobil uygulama cihazın güvenli donanımında (iOS Secure
/// Enclave, Android Keystore) bir ECDSA P-256 anahtar çifti üretir; gizli anahtar cihazdan hiç çıkmaz, sunucuya yalnızca
/// açık anahtar kaydedilir. Sonraki girişlerde sunucu tek kullanımlık bir metin verir, cihaz imzalar; imza doğruysa istek
/// gerçekten o cihazdan gelmektedir (şifre çalınsa bile başka cihazdan bu kanıt üretilemez). Anahtarın kullanımı cihazda
/// biyometriye bağlanabilir (Face ID / parmak izi).
/// </summary>
public interface IDeviceBindingService
{
    /// <summary>
    /// İstemcinin gönderdiği açık anahtarı doğrular ve saklanacak biçime (SubjectPublicKeyInfo, DER) çevirir.
    /// </summary>
    /// <param name="publicKey">Base64 (ya da base64url) SubjectPublicKeyInfo; P-256 olmalı.</param>
    /// <exception cref="ArgumentException">Geçersiz ya da P-256 olmayan anahtar.</exception>
    byte[] ParsePublicKey(string publicKey);

    /// <summary>Bu cihaz için tek kullanımlık meydan okuma (varsayılan 2 dakika geçerli).</summary>
    /// <param name="deviceId">Cihaz kimliği; meydan okuma yalnızca bu cihaz için geçerlidir.</param>
    DeviceChallenge CreateChallenge(string deviceId);

    /// <summary>İmzayı doğrular.</summary>
    /// <param name="publicKey">Cihazın kayıtlı açık anahtarı (<see cref="ParsePublicKey"/>).</param>
    /// <param name="deviceId">Cihaz kimliği.</param>
    /// <param name="challenge">Cihaza verilen meydan okuma.</param>
    /// <param name="signature">Base64 imza: DER (Android, iOS <c>ecdsaSignatureMessageX962SHA256</c>) ya da 64 bayt r||s.</param>
    /// <param name="lastIssuedAt">Bu cihazda en son kullanılan meydan okumanın zamanı; daha eski ya da aynısı reddedilir (tekrar).</param>
    DeviceVerification Verify(byte[] publicKey, string deviceId, string challenge, string signature, DateTimeOffset? lastIssuedAt);
}

public sealed class DeviceBindingService : IDeviceBindingService
{
    private const byte Version = 1;
    private const int RandomLength = 16;
    private const int MacLength = 32;
    private const int PayloadLength = 1 + 8 + RandomLength; // sürüm + zaman + rastgele

    private readonly byte[] _key;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;

    /// <param name="secretKey">En az 32 karakterlik gizli anahtar (<c>CanSecurityOptions.VerificationCodeKey</c>; bu kullanım için ayrı anahtar türetilir).</param>
    /// <param name="timeProvider">Saat.</param>
    /// <param name="challengeLifetime">Meydan okumanın geçerlilik süresi (varsayılan 2 dakika).</param>
    public DeviceBindingService(string secretKey, TimeProvider timeProvider, TimeSpan? challengeLifetime = null)
    {
        if (Encoding.UTF8.GetByteCount(secretKey ?? string.Empty) < 32)
            throw new ArgumentException("Cihaz doğrulama anahtarı en az 32 karakter olmalı.", nameof(secretKey));
        ArgumentNullException.ThrowIfNull(timeProvider);

        _key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secretKey!), "can-device-binding"u8);
        _timeProvider = timeProvider;
        _lifetime = challengeLifetime ?? TimeSpan.FromMinutes(2);
    }

    public byte[] ParsePublicKey(string publicKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKey);
        byte[] der;
        try
        {
            der = Base64UrlEncoder.DecodeBytes(publicKey.Trim().Replace('+', '-').Replace('/', '_').TrimEnd('='));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Açık anahtar base64 değil.", nameof(publicKey), exception);
        }

        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(der, out int read);
            if (read != der.Length)
                throw new ArgumentException("Açık anahtarın sonunda fazladan veri var.", nameof(publicKey));
        }
        catch (CryptographicException exception)
        {
            throw new ArgumentException("Açık anahtar SubjectPublicKeyInfo (DER) biçiminde bir ECDSA anahtarı olmalı.", nameof(publicKey), exception);
        }

        if (ecdsa.KeySize != 256 || !IsP256(ecdsa.ExportParameters(false).Curve))
            throw new ArgumentException("Açık anahtar P-256 (secp256r1) olmalı.", nameof(publicKey));

        return ecdsa.ExportSubjectPublicKeyInfo(); // kanonik biçim
    }

    public DeviceChallenge CreateChallenge(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        byte[] token = new byte[PayloadLength + MacLength];
        token[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(token.AsSpan(1, 8), now.ToUnixTimeMilliseconds());
        RandomNumberGenerator.Fill(token.AsSpan(9, RandomLength));
        Mac(deviceId, token.AsSpan(0, PayloadLength), token.AsSpan(PayloadLength));

        return new DeviceChallenge(Base64UrlEncoder.Encode(token), now + _lifetime);
    }

    public DeviceVerification Verify(byte[] publicKey, string deviceId, string challenge, string signature, DateTimeOffset? lastIssuedAt)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(challenge) || string.IsNullOrWhiteSpace(signature))
            return DeviceVerification.Fail("eksik alan");

        byte[] token;
        byte[] signatureBytes;
        try
        {
            token = Base64UrlEncoder.DecodeBytes(challenge);
            signatureBytes = Convert.FromBase64String(signature.Trim().Replace('-', '+').Replace('_', '/').PadRight((signature.Trim().Length + 3) / 4 * 4, '='));
        }
        catch (FormatException)
        {
            return DeviceVerification.Fail("biçim");
        }

        if (token.Length != PayloadLength + MacLength || token[0] != Version)
            return DeviceVerification.Fail("biçim");

        Span<byte> expected = stackalloc byte[MacLength];
        Mac(deviceId, token.AsSpan(0, PayloadLength), expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, token.AsSpan(PayloadLength)))
            return DeviceVerification.Fail("meydan okuma bu cihaz için üretilmemiş");

        DateTimeOffset issuedAt = DateTimeOffset.FromUnixTimeMilliseconds(BinaryPrimitives.ReadInt64BigEndian(token.AsSpan(1, 8)));
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (now > issuedAt + _lifetime || issuedAt > now + TimeSpan.FromMinutes(1))
            return DeviceVerification.Fail("süresi dolmuş");
        if (lastIssuedAt is { } last && issuedAt <= last)
            return DeviceVerification.Fail("daha önce kullanılmış");

        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        }
        catch (CryptographicException)
        {
            return DeviceVerification.Fail("kayıtlı anahtar okunamadı");
        }

        byte[] data = Encoding.UTF8.GetBytes(challenge);
        bool valid;
        try
        {
            valid = signatureBytes.Length == 64
                ? ecdsa.VerifyData(data, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
                : ecdsa.VerifyData(data, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException)
        {
            valid = false; // bozuk DER
        }

        return valid ? new DeviceVerification(true, issuedAt, null) : DeviceVerification.Fail("imza geçersiz");
    }

    private static bool IsP256(ECCurve curve) =>
        curve.IsNamed
        && (curve.Oid.Value == "1.2.840.10045.3.1.7"
            || curve.Oid.FriendlyName is "nistP256" or "ECDSA_P256" or "secp256r1" or "prime256v1");

    private void Mac(string deviceId, ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, _key);
        hmac.AppendData(Encoding.UTF8.GetBytes(deviceId));
        hmac.AppendData(new byte[] { 0 });
        hmac.AppendData(payload);
        hmac.GetHashAndReset(destination);
    }
}
