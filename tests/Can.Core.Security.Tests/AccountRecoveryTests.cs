using Can.Core.Security.Entities;
using Can.Core.Security.RecoveryCodes;
using Can.Core.Security.Tokens;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Can.Core.Security.Tests;

public class RecoveryCodeTests
{
    private const string Key = "kurtarma-kodu-anahtari-en-az-32-bayt!!";

    [Fact]
    public void Codes_are_unique_readable_and_stored_as_keyed_hashes()
    {
        var service = new RecoveryCodeService(Key);

        RecoveryCodeSet set = service.Generate();

        Assert.Equal(10, set.Codes.Count);
        Assert.Equal(10, set.Codes.Distinct().Count());
        Assert.All(set.Codes, code => Assert.Matches("^[A-HJ-NP-Z2-9]{5}-[A-HJ-NP-Z2-9]{5}$", code));
        Assert.Equal(set.Codes.Select(service.Hash), set.CodeHashes);
        Assert.DoesNotContain(set.CodeHashes, h => set.Codes.Any(c => h.Contains(c.Replace("-", ""), StringComparison.Ordinal)));
    }

    [Fact]
    public void Hash_ignores_case_dashes_and_spaces_but_depends_on_the_key()
    {
        var service = new RecoveryCodeService(Key);

        Assert.Equal(service.Hash("ABCDE-FGH23"), service.Hash(" abcde fgh23 "));
        Assert.NotEqual(service.Hash("ABCDE-FGH23"), service.Hash("ABCDE-FGH24"));
        Assert.NotEqual(service.Hash("ABCDE-FGH23"), new RecoveryCodeService(Key + "x").Hash("ABCDE-FGH23"));
        Assert.Throws<InvalidOperationException>(() => new RecoveryCodeService("kisa"));
    }

    [Fact]
    public void Recovery_code_is_single_use()
    {
        var code = new RecoveryCode<Guid>(Guid.NewGuid(), "HASH", DateTimeOffset.UtcNow);

        code.MarkUsed(DateTimeOffset.UtcNow);

        Assert.True(code.IsUsed);
        Assert.Throws<InvalidOperationException>(() => code.MarkUsed(DateTimeOffset.UtcNow));
    }
}

public class SecurityStampTests
{
    private sealed class TestUser : User<Guid>
    {
        public TestUser()
            : base(Guid.NewGuid(), "ada@test.local") { }
    }

    [Fact]
    public void Password_change_rotates_stamp_but_rehash_does_not()
    {
        var user = new TestUser();
        string initial = user.SecurityStamp;

        user.UpgradePasswordHash("yeni-iterasyonla-ayni-sifre");
        Assert.Equal(initial, user.SecurityStamp);

        user.SetPasswordHash("yeni-sifre");
        Assert.NotEqual(initial, user.SecurityStamp);
    }

    [Fact]
    public void Refresh_token_is_invalid_after_the_stamp_changes()
    {
        var user = new TestUser();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var token = new RefreshToken<Guid>(user.Id, new RefreshTokenValue("raw", "HASH", now.AddDays(7)), now, "127.0.0.1", user.SecurityStamp);
        var legacy = new RefreshToken<Guid>(user.Id, new RefreshTokenValue("raw2", "HASH2", now.AddDays(7)), now, "127.0.0.1");

        Assert.True(token.IsValidFor(now, user.SecurityStamp));

        user.SetAuthenticator(AuthenticatorType.Otp);

        Assert.False(token.IsValidFor(now, user.SecurityStamp));
        Assert.True(legacy.IsValidFor(now, user.SecurityStamp)); // damgasız eski kayıt denetlenmez
        Assert.False(token.IsValidFor(now.AddDays(8), token.SecurityStamp!)); // süre dolunca damga da kurtarmaz
    }

    [Fact]
    public void Access_token_carries_the_stamp_only_when_given()
    {
        var tokens = new TokenService(new JwtOptions { Issuer = "i", Audience = "a", SigningKey = new string('k', 32) }, TimeProvider.System);

        var stamped = new JsonWebToken(tokens.CreateAccessToken(new TokenSubject("42", SecurityStamp: "ABC123")).Token);
        var plain = new JsonWebToken(tokens.CreateAccessToken(new TokenSubject("42")).Token);

        Assert.Equal("ABC123", stamped.GetClaim(TokenService.SecurityStampClaimType).Value);
        Assert.False(plain.TryGetClaim(TokenService.SecurityStampClaimType, out _));
    }
}
