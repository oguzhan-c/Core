using System.Security.Cryptography;
using System.Text;
using CanFx.Security.Devices;
using CanFx.Security.Entities;
using CanFx.Security.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;

namespace CanFx.Security.Tests;

public class SessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rotation_keeps_the_session_and_device_and_closes_the_old_token()
    {
        var first = new RefreshToken<Guid>(Guid.NewGuid(), new RefreshTokenValue("a", "HASH-A", Now.AddDays(7)), Now, "1.1.1.1", "STAMP",
            new SessionDevice("device-1", "Ayşe'nin iPhone'u", "App/1.0 iOS"));

        RefreshToken<Guid> second = first.Rotate(new RefreshTokenValue("b", "HASH-B", Now.AddDays(8)), Now.AddHours(1), "2.2.2.2", "STAMP");

        Assert.Equal(32, first.SessionId.Length);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(Now, second.SessionStartedAt);
        Assert.Equal("device-1", second.DeviceId);
        Assert.Equal("Ayşe'nin iPhone'u", second.DeviceName);
        Assert.Equal("App/1.0 iOS", second.UserAgent);
        Assert.Equal(Now.AddHours(1), second.LastUsedAt);
        Assert.Equal("2.2.2.2", second.LastUsedIp);

        Assert.True(first.IsRevoked);
        Assert.Equal(RefreshToken<Guid>.RotatedReason, first.RevokedReason);
        Assert.Equal("HASH-B", first.ReplacedByTokenHash);
        Assert.Throws<InvalidOperationException>(() => first.Rotate(new RefreshTokenValue("c", "HASH-C", Now.AddDays(9)), Now, null, null));
    }

    [Fact]
    public void Each_login_starts_a_new_session_and_client_values_are_trimmed()
    {
        var a = new RefreshToken<Guid>(Guid.NewGuid(), new RefreshTokenValue("a", "A", Now.AddDays(7)), Now, null);
        var b = new RefreshToken<Guid>(Guid.NewGuid(), new RefreshTokenValue("b", "B", Now.AddDays(7)), Now, null, null,
            new SessionDevice(DeviceName: new string('x', 500), UserAgent: "  "));

        Assert.NotEqual(a.SessionId, b.SessionId);
        Assert.Equal(SessionDevice.DeviceNameMaxLength, b.DeviceName!.Length);
        Assert.Null(b.UserAgent);
    }

    [Fact]
    public void Session_id_is_written_to_the_access_token()
    {
        var service = new TokenService(new JwtOptions { Issuer = "i", Audience = "a", SigningKey = new string('k', 64) }, new FixedClock(Now));
        AccessToken token = service.CreateAccessToken(new TokenSubject("42", SessionId: "abc"));

        Assert.Equal("abc", new JsonWebToken(token.Token).GetClaim("sid").Value);
    }
}

public class DeviceBindingTests
{
    private const string Key = "cihaz-baglama-icin-en-az-otuz-iki-karakterlik-anahtar";

    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 14, 9, 0, 0, TimeSpan.Zero));
    private readonly DeviceBindingService _service;
    private readonly ECDsa _deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public DeviceBindingTests() => _service = new DeviceBindingService(Key, _clock);

    private byte[] RegisteredKey() => _service.ParsePublicKey(Convert.ToBase64String(_deviceKey.ExportSubjectPublicKeyInfo()));

    private string Sign(string challenge, DSASignatureFormat format = DSASignatureFormat.Rfc3279DerSequence) =>
        Convert.ToBase64String(_deviceKey.SignData(Encoding.UTF8.GetBytes(challenge), HashAlgorithmName.SHA256, format));

    [Theory]
    [InlineData(DSASignatureFormat.Rfc3279DerSequence)]      // Android Keystore, iOS SecKey
    [InlineData(DSASignatureFormat.IeeeP1363FixedFieldConcatenation)] // WebCrypto
    public void Signature_from_the_registered_device_is_accepted(DSASignatureFormat format)
    {
        DeviceChallenge challenge = _service.CreateChallenge("device-1");

        DeviceVerification result = _service.Verify(RegisteredKey(), "device-1", challenge.Challenge, Sign(challenge.Challenge, format), lastIssuedAt: null);

        Assert.True(result.IsValid, result.Failure);
        Assert.Equal(_clock.Now.ToUnixTimeMilliseconds(), result.IssuedAt.ToUnixTimeMilliseconds());
        Assert.Equal(_clock.Now.AddMinutes(2), challenge.ExpiresAt);
    }

    [Fact]
    public void Another_key_another_device_or_a_tampered_challenge_is_rejected()
    {
        DeviceChallenge challenge = _service.CreateChallenge("device-1");
        string signature = Sign(challenge.Challenge);

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] strangerKey = _service.ParsePublicKey(Convert.ToBase64String(stranger.ExportSubjectPublicKeyInfo()));
        Assert.False(_service.Verify(strangerKey, "device-1", challenge.Challenge, signature, null).IsValid);

        // başka cihaz için üretilmiş meydan okuma bu cihazda kullanılamaz
        Assert.Equal("meydan okuma bu cihaz için üretilmemiş", _service.Verify(RegisteredKey(), "device-2", challenge.Challenge, signature, null).Failure);

        char[] tampered = challenge.Challenge.ToCharArray();
        tampered[5] = tampered[5] == 'A' ? 'B' : 'A';
        Assert.False(_service.Verify(RegisteredKey(), "device-1", new string(tampered), Sign(new string(tampered)), null).IsValid);
    }

    [Fact]
    public void Challenges_expire_and_cannot_be_replayed()
    {
        DeviceChallenge challenge = _service.CreateChallenge("device-1");
        string signature = Sign(challenge.Challenge);
        DeviceVerification first = _service.Verify(RegisteredKey(), "device-1", challenge.Challenge, signature, null);
        Assert.True(first.IsValid);

        Assert.Equal("daha önce kullanılmış", _service.Verify(RegisteredKey(), "device-1", challenge.Challenge, signature, first.IssuedAt).Failure);

        _clock.Now = _clock.Now.AddMinutes(3);
        Assert.Equal("süresi dolmuş", _service.Verify(RegisteredKey(), "device-1", challenge.Challenge, signature, null).Failure);
    }

    [Fact]
    public void Only_p256_public_keys_are_accepted()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Throws<ArgumentException>(() => _service.ParsePublicKey(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));
        using var rsa = RSA.Create(2048);
        Assert.Throws<ArgumentException>(() => _service.ParsePublicKey(Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo())));
        Assert.Throws<ArgumentException>(() => _service.ParsePublicKey("bu bir anahtar değil"));

        // base64url da kabul edilir
        string url = Convert.ToBase64String(_deviceKey.ExportSubjectPublicKeyInfo()).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        Assert.Equal(RegisteredKey(), _service.ParsePublicKey(url));
    }

    [Fact]
    public void Short_secret_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new DeviceBindingService("kısa", _clock));
    }
}
