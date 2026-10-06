using System.Security.Claims;
using System.Text;
using Can.Core.Security.DependencyInjection;
using Can.Core.Security.Entities;
using Can.Core.Security.Hashing;
using Can.Core.Security.Otp;
using Can.Core.Security.Passkeys;
using Can.Core.Security.Tokens;
using Can.Core.Security.VerificationCodes;
using Fido2NetLib;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Can.Core.Security.Tests;

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

public class PasswordHasherTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new(iterations: 10_000);

    [Fact]
    public void Correct_password_verifies_and_wrong_password_fails()
    {
        string hash = _hasher.Hash("Gizli-Şifre-123");

        Assert.Equal(PasswordVerificationResult.Success, _hasher.Verify("Gizli-Şifre-123", hash));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify("gizli-şifre-123", hash));
    }

    [Fact]
    public void Same_password_produces_different_hashes()
    {
        Assert.NotEqual(_hasher.Hash("aynı"), _hasher.Hash("aynı"));
    }

    [Fact]
    public void Tampered_or_garbage_hashes_fail()
    {
        string hash = _hasher.Hash("pw");
        string tampered = hash[..^4] + (hash[^4] == 'A' ? "BBBB" : "AAAA");

        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify("pw", tampered));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify("pw", "çöp"));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify("pw", ""));
    }

    [Fact]
    public void Old_iteration_count_requests_rehash()
    {
        string oldHash = new Pbkdf2PasswordHasher(10_000).Hash("pw");

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, new Pbkdf2PasswordHasher(20_000).Verify("pw", oldHash));
    }
}

public class TokenServiceTests
{
    private static readonly JwtOptions Options = new()
    {
        Issuer = "can-tests",
        Audience = "can-clients",
        SigningKey = "bu-anahtar-en-az-otuz-iki-karakter-olmali!",
    };

    private readonly TokenService _tokens = new(Options, TimeProvider.System);

    [Fact]
    public async Task Access_token_contains_claims_and_validates()
    {
        AccessToken token = _tokens.CreateAccessToken(
            new TokenSubject("u-1", "ada", "ada@test.local", Roles: ["Admin", "Editor"], TenantId: "t-1")
        );

        TokenValidationResult result = await new JsonWebTokenHandler().ValidateTokenAsync(token.Token, _tokens.CreateValidationParameters());

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal("u-1", result.ClaimsIdentity.FindFirst("sub")?.Value);
        Assert.Equal("ada@test.local", result.ClaimsIdentity.FindFirst("email")?.Value);
        Assert.Equal(new[] { "Admin", "Editor" }, result.ClaimsIdentity.FindAll("role").Select(c => c.Value).Order());
        Assert.Equal("t-1", result.ClaimsIdentity.FindFirst("tenant_id")?.Value);
        Assert.True(token.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        var other = new TokenService(
            new JwtOptions { Issuer = Options.Issuer, Audience = Options.Audience, SigningKey = new string('x', 40) },
            TimeProvider.System
        );

        string token = other.CreateAccessToken(new TokenSubject("u-1")).Token;
        TokenValidationResult result = await new JsonWebTokenHandler().ValidateTokenAsync(token, _tokens.CreateValidationParameters());

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Short_signing_key_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new TokenService(new JwtOptions { Issuer = "i", Audience = "a", SigningKey = "kisa" }, TimeProvider.System)
        );
    }

    [Fact]
    public void Refresh_tokens_are_random_and_stored_as_hash()
    {
        RefreshTokenValue first = _tokens.CreateRefreshToken();
        RefreshTokenValue second = _tokens.CreateRefreshToken();

        Assert.NotEqual(first.Token, second.Token);
        Assert.NotEqual(first.Token, first.TokenHash);
        Assert.Equal(first.TokenHash, _tokens.HashRefreshToken(first.Token));
        Assert.True(first.Token.Length >= 43); // 256 bit
    }
}

public class TotpTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory] // RFC 6238 Ek B, SHA1
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(20000000000L, "65353130")]
    public void Matches_rfc6238_test_vectors(long unixTime, string expected)
    {
        Assert.Equal(expected, TotpService.ComputeCode(RfcSecret, unixTime / 30, digits: 8));
    }

    [Fact]
    public void Verifies_current_and_adjacent_steps_but_not_replays()
    {
        var clock = new FixedClock(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000));
        var totp = new TotpService(clock);
        byte[] secret = totp.GenerateSecret();
        long step = 1_800_000_000 / 30;

        string current = TotpService.ComputeCode(secret, step, 6);
        Assert.True(totp.TryVerify(secret, current, lastUsedTimeStep: null, out long matched));
        Assert.Equal(step, matched);

        // aynı kod ikinci kez kullanılamaz
        Assert.False(totp.TryVerify(secret, current, lastUsedTimeStep: matched, out _));

        // saat farkı: bir önceki adım kabul, iki adım öncesi red
        Assert.True(totp.TryVerify(secret, TotpService.ComputeCode(secret, step - 1, 6), null, out _));
        Assert.False(totp.TryVerify(secret, TotpService.ComputeCode(secret, step - 2, 6), null, out _));

        Assert.False(totp.TryVerify(secret, "abc123", null, out _));
        Assert.False(totp.TryVerify(secret, "", null, out _));
    }

    [Fact]
    public void Provisioning_uri_contains_secret_and_issuer()
    {
        var totp = new TotpService(TimeProvider.System);
        byte[] secret = totp.GenerateSecret();

        string uri = totp.GetProvisioningUri(secret, "Can App", "ada@test.local");

        Assert.StartsWith("otpauth://totp/", uri);
        Assert.Contains($"secret={Base32.Encode(secret)}", uri);
        Assert.Contains("issuer=Can%20App", uri);
    }

    [Theory] // RFC 4648
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_rfc4648(string input, string expected)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(input);

        Assert.Equal(expected, Base32.Encode(bytes));
        Assert.Equal(bytes, Base32.Decode(expected));
    }
}

public class VerificationCodeTests
{
    private const string Key = "dogrulama-kodu-anahtari-en-az-32-karakter";

    [Fact]
    public void Generated_code_verifies_only_with_same_key()
    {
        var clock = new FixedClock(DateTimeOffset.UtcNow);
        var service = new VerificationCodeService(Key, clock);

        VerificationCode code = service.Generate();

        Assert.Matches("^[0-9]{6}$", code.Code);
        Assert.True(service.Verify(code.Code, code.CodeHash));
        Assert.False(service.Verify("000000" == code.Code ? "111111" : "000000", code.CodeHash));
        Assert.False(new VerificationCodeService(Key + "-baska", clock).Verify(code.Code, code.CodeHash));
        Assert.Equal(clock.Now.AddMinutes(10), code.ExpiresAt);
    }

    [Fact]
    public void Short_key_is_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new VerificationCodeService("kisa", TimeProvider.System));
    }
}

public class EntityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    private sealed class AppUser : User<Guid>
    {
        public AppUser(string email)
            : base(Guid.CreateVersion7(), email) { }
    }

    [Fact]
    public void User_email_is_normalized_and_password_change_rotates_stamp()
    {
        var user = new AppUser("  Ada@Test.Local ");
        string stamp = user.SecurityStamp;

        user.SetPasswordHash("hash");

        Assert.Equal("Ada@Test.Local", user.Email);
        Assert.Equal("ADA@TEST.LOCAL", user.NormalizedEmail);
        Assert.NotEqual(stamp, user.SecurityStamp);
        Assert.Equal(32, user.PasskeyUserHandle.Length);
    }

    [Fact]
    public void User_is_locked_out_after_max_failed_attempts()
    {
        var user = new AppUser("a@b.c");

        for (int i = 0; i < 4; i++)
            user.RegisterFailedAccess(Now);
        Assert.False(user.IsLockedOut(Now));

        user.RegisterFailedAccess(Now);
        Assert.True(user.IsLockedOut(Now));
        Assert.False(user.IsLockedOut(Now.AddMinutes(16)));

        user.ResetAccessFailed();
        Assert.False(user.IsLockedOut(Now));
    }

    [Fact]
    public void Refresh_token_lifecycle()
    {
        var token = new RefreshToken<Guid>(Guid.NewGuid(), new RefreshTokenValue("t", "HASH", Now.AddDays(7)), Now, "1.2.3.4");

        Assert.True(token.IsActive(Now));
        Assert.False(token.IsActive(Now.AddDays(8)));

        token.Revoke(Now, "1.2.3.4", "Yenisiyle değiştirildi", replacedByTokenHash: "NEW");

        Assert.True(token.IsRevoked);
        Assert.False(token.IsActive(Now));
        Assert.Equal("NEW", token.ReplacedByTokenHash);
    }

    [Fact]
    public void Email_authenticator_limits_attempts_and_expires()
    {
        var authenticator = new EmailAuthenticator<Guid>(Guid.NewGuid());
        Assert.False(authenticator.CanAttempt(Now)); // henüz kod yok

        authenticator.SetCode(new VerificationCode("123456", "HASH", Now.AddMinutes(10)));
        Assert.True(authenticator.CanAttempt(Now));
        Assert.False(authenticator.CanAttempt(Now.AddMinutes(11)));

        for (int i = 0; i < EmailAuthenticator<Guid>.DefaultMaxAttempts; i++)
            authenticator.RegisterFailedAttempt();
        Assert.False(authenticator.CanAttempt(Now));

        authenticator.MarkVerified();
        Assert.True(authenticator.IsVerified);
        Assert.Null(authenticator.CodeHash);
    }

    [Fact]
    public void Otp_authenticator_records_used_step()
    {
        var authenticator = new OtpAuthenticator<Guid>(Guid.NewGuid(), [1, 2, 3]);

        authenticator.MarkCodeUsed(42);

        Assert.True(authenticator.IsVerified);
        Assert.Equal(42, authenticator.LastUsedTimeStep);
    }

    [Fact]
    public void Role_name_is_normalized()
    {
        var role = new Role<Guid>(" Product.Write ");

        Assert.Equal("Product.Write", role.Name);
        Assert.Equal("PRODUCT.WRITE", role.NormalizedName);
    }
}

public class PasskeyTests
{
    private static PasskeyService CreateService() =>
        new(
            new Fido2(
                new Fido2Configuration
                {
                    RPID = "localhost",
                    RPName = "Can Tests",
                    Origins = new HashSet<string> { "https://localhost" },
                }
            )
        );

    [Fact]
    public void Registration_options_contain_user_challenge_and_excluded_credentials()
    {
        byte[] handle = [1, 2, 3, 4];
        CredentialCreateOptions options = CreateService().BeginRegistration(new PasskeyUser(handle, "ada", "Ada"), [[9, 9]]);

        Assert.Equal(handle, options.User.Id);
        Assert.NotEmpty(options.Challenge);
        Assert.Single(options.ExcludeCredentials);

        // Seçenekler sunucuda JSON olarak saklanıp geri okunabilmeli
        CredentialCreateOptions restored = CredentialCreateOptions.FromJson(options.ToJson());
        Assert.Equal(options.Challenge, restored.Challenge);
    }

    [Fact]
    public void Login_options_without_credentials_allow_usernameless_login()
    {
        AssertionOptions options = CreateService().BeginLogin();

        Assert.NotEmpty(options.Challenge);
        Assert.Empty(options.AllowCredentials);
    }
}

public class RegistrationTests
{
    [Fact]
    public void AddCanSecurity_registers_services_and_optional_features()
    {
        var services = new ServiceCollection();
        services.AddCanSecurity(o =>
        {
            o.Jwt = new JwtOptions { Issuer = "i", Audience = "a", SigningKey = new string('k', 32) };
            o.VerificationCodeKey = new string('v', 32);
        });

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IPasswordHasher>());
        Assert.NotNull(provider.GetService<ITokenService>());
        Assert.NotNull(provider.GetService<ITotpService>());
        Assert.NotNull(provider.GetService<IVerificationCodeService>());
        Assert.Null(provider.GetService<IPasskeyService>()); // passkey ayarlanmadı
    }

    [Fact]
    public void Invalid_jwt_options_fail_at_startup()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddCanSecurity(o => o.Jwt = new JwtOptions { Issuer = "i", Audience = "a", SigningKey = "kisa" })
        );
    }

    [Fact]
    public void Claims_of_custom_types_can_be_added()
    {
        var tokens = new TokenService(new JwtOptions { Issuer = "i", Audience = "a", SigningKey = new string('k', 32) }, TimeProvider.System);

        AccessToken token = tokens.CreateAccessToken(new TokenSubject("u", AdditionalClaims: [new Claim("plan", "pro")]));

        Assert.Contains("plan", new JsonWebToken(token.Token).Claims.Select(c => c.Type));
    }

    [Fact]
    public void Permissions_are_written_as_permission_claims()
    {
        var tokens = new TokenService(new JwtOptions { Issuer = "i", Audience = "a", SigningKey = new string('k', 32) }, TimeProvider.System);

        AccessToken token = tokens.CreateAccessToken(new TokenSubject("u", Permissions: ["products.*", "orders.cancel", "ORDERS.CANCEL"]));

        string[] permissions = new JsonWebToken(token.Token).Claims.Where(c => c.Type == "permission").Select(c => c.Value).Order().ToArray();
        Assert.Equal(new[] { "orders.cancel", "products.*" }, permissions);
    }
}
