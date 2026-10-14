using Can.Core.Persistence.Context;
using Can.Core.Security.Entities;
using Can.Core.Security.Tokens;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Security.EntityFrameworkCore.Tests;

/// <summary>Testte şifrelemeyi görünür kılar: baytları ters çevirip önüne "X" koyar.</summary>
public sealed class ReversingProtector : ISecretProtector
{
    public byte[] Protect(byte[] value) => [(byte)'X', .. value.Reverse()];

    public byte[] Unprotect(byte[] value) => value[0] == (byte)'X' ? [.. value[1..].Reverse()] : throw new InvalidOperationException("şifreli değil");
}

public sealed class ProtectedAuthDbContext(DbContextOptions<ProtectedAuthDbContext> options) : CanDbContext(options)
{
    public static readonly ReversingProtector Protector = new();

    protected override void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyCanSecurityModel<AppUser, Guid>(o => o.SecretProtector = Protector);
}

public sealed class AccountProtectionTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        await using AuthDbContext db = Plain();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private AuthDbContext Plain() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(_connection).Options);

    private ProtectedAuthDbContext Protected() => new(new DbContextOptionsBuilder<ProtectedAuthDbContext>().UseSqlite(_connection).Options);

    private async Task<byte[]> RawSecretAsync(Guid userId)
    {
        await using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT SecretKey FROM OtpAuthenticators WHERE UserId = $id";
        command.Parameters.AddWithValue("$id", userId.ToString().ToUpperInvariant());
        object? value = await command.ExecuteScalarAsync();
        if (value is null)
        {
            command.Parameters["$id"].Value = userId.ToString();
            value = await command.ExecuteScalarAsync();
        }

        return (byte[])value!;
    }

    [Fact]
    public async Task Otp_secrets_are_encrypted_and_legacy_plain_secrets_still_read()
    {
        byte[] secret = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
        var plainUser = new AppUser("eski@test.local", "Eski");
        var newUser = new AppUser("yeni@test.local", "Yeni");

        await using (AuthDbContext db = Plain()) // şifreleme açılmadan önce kaydedilmiş
        {
            db.Users.Add(plainUser);
            db.OtpAuthenticators.Add(new OtpAuthenticator<Guid>(plainUser.Id, secret));
            await db.SaveChangesAsync();
        }

        await using (ProtectedAuthDbContext db = Protected())
        {
            db.Set<AppUser>().Add(newUser);
            db.Set<OtpAuthenticator<Guid>>().Add(new OtpAuthenticator<Guid>(newUser.Id, secret));
            await db.SaveChangesAsync();
        }

        byte[] stored = await RawSecretAsync(newUser.Id);
        Assert.True(ProtectedSecret.IsProtected(stored));
        Assert.NotEqual(secret, stored);
        Assert.False(ProtectedSecret.IsProtected(await RawSecretAsync(plainUser.Id)));

        await using (ProtectedAuthDbContext db = Protected())
        {
            Dictionary<Guid, byte[]> secrets = await db.Set<OtpAuthenticator<Guid>>().ToDictionaryAsync(a => a.UserId, a => a.SecretKey);
            Assert.Equal(secret, secrets[newUser.Id]);
            Assert.Equal(secret, secrets[plainUser.Id]); // eski düz kayıt da okunur

            Assert.Equal(2, await ProtectedSecret.ProtectExistingOtpSecretsAsync<Guid>(db));
        }

        Assert.True(ProtectedSecret.IsProtected(await RawSecretAsync(plainUser.Id)));
        await using (ProtectedAuthDbContext db = Protected())
            Assert.Equal(secret, (await db.Set<OtpAuthenticator<Guid>>().SingleAsync(a => a.UserId == plainUser.Id)).SecretKey);
    }

    [Fact]
    public async Task Recovery_codes_are_unique_per_user()
    {
        var user = new AppUser("kod@test.local", "Kod");
        await using AuthDbContext db = Plain();
        db.Users.Add(user);
        db.Set<RecoveryCode<Guid>>().Add(new RecoveryCode<Guid>(user.Id, "AAA", DateTimeOffset.UtcNow));
        db.Set<RecoveryCode<Guid>>().Add(new RecoveryCode<Guid>(user.Id, "AAA", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Stamp_validator_reads_the_user_and_caches_until_invalidated()
    {
        var user = new AppUser("damga@test.local", "Damga");
        await using (AuthDbContext db = Plain())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(o => o.UseSqlite(_connection));
        services.AddCanSecurityStampValidator<AuthDbContext, AppUser, Guid>(o => o.CacheDuration = TimeSpan.FromMinutes(5));
        await using ServiceProvider provider = services.BuildServiceProvider();

        async Task<bool> IsCurrent(string id, string stamp)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ISecurityStampValidator>().IsCurrentAsync(id, stamp);
        }

        string original = user.SecurityStamp;
        Assert.True(await IsCurrent(user.Id.ToString(), original));
        Assert.False(await IsCurrent(user.Id.ToString(), "baska"));
        Assert.False(await IsCurrent(Guid.NewGuid().ToString(), original)); // kullanıcı yok
        Assert.False(await IsCurrent("guid-degil", original));

        await using (AuthDbContext db = Plain())
        {
            AppUser loaded = await db.Users.SingleAsync(u => u.Id == user.Id);
            loaded.SetPasswordHash("yeni");
            await db.SaveChangesAsync();
        }

        Assert.True(await IsCurrent(user.Id.ToString(), original)); // önbellekte eski damga (süre dolana kadar)

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            scope.ServiceProvider.GetRequiredService<ISecurityStampValidator>().Invalidate(user.Id.ToString());

        Assert.False(await IsCurrent(user.Id.ToString(), original));
    }
}
