using Can.Core.Persistence.Context;
using Can.Core.Security.Entities;
using Can.Core.Security.Passkeys;
using Can.Core.Security.Tokens;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Can.Core.Security.EntityFrameworkCore.Tests;

public sealed class AppUser : User<Guid>
{
    private AppUser() { }

    public AppUser(string email, string fullName)
        : base(Guid.CreateVersion7(), email) => FullName = fullName;

    public string FullName { get; private set; } = "";

    public void AddRole(Role<Guid> role) => UserRoles.Add(new UserRole<Guid>(Id, role.Id));
}

public sealed class AuthDbContext(DbContextOptions<AuthDbContext> options) : CanDbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role<Guid>> Roles => Set<Role<Guid>>();
    public DbSet<RefreshToken<Guid>> RefreshTokens => Set<RefreshToken<Guid>>();
    public DbSet<UserPasskey<Guid>> Passkeys => Set<UserPasskey<Guid>>();
    public DbSet<OtpAuthenticator<Guid>> OtpAuthenticators => Set<OtpAuthenticator<Guid>>();

    protected override void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyCanSecurityModel<AppUser, Guid>();
}

/// <summary>EF modeli context tipine göre önbelleğe alır; farklı ayar için ayrı tip.</summary>
public sealed class PrefixedAuthDbContext(DbContextOptions<PrefixedAuthDbContext> options) : CanDbContext(options)
{
    protected override void ConfigureModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyCanSecurityModel<AppUser, Guid>(o =>
        {
            o.TablePrefix = "Auth";
            o.Schema = "identity";
        });
}

public sealed class SecurityModelTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        await using AuthDbContext db = Create();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private AuthDbContext Create() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(_connection).Options);

    private static RefreshTokenValue Token(string hash) => new("raw-" + hash, hash, DateTimeOffset.UtcNow.AddDays(7));

    [Fact]
    public async Task User_roles_tokens_and_authenticators_round_trip()
    {
        var admin = new Role<Guid>(Guid.CreateVersion7(), "Admin");
        var user = new AppUser("ada@test.local", "Ada Lovelace");
        user.AddRole(admin);

        await using (AuthDbContext db = Create())
        {
            db.Roles.Add(admin);
            db.Users.Add(user);
            db.RefreshTokens.Add(new RefreshToken<Guid>(user.Id, Token("hash-1"), DateTimeOffset.UtcNow, "127.0.0.1"));
            db.Passkeys.Add(new UserPasskey<Guid>(user.Id, new PasskeyCredential([1, 2, 3], [4, 5], user.PasskeyUserHandle, uint.MaxValue, Guid.NewGuid(), "internal", true), "iPhone", DateTimeOffset.UtcNow));
            db.OtpAuthenticators.Add(new OtpAuthenticator<Guid>(user.Id, [9, 9, 9]));
            await db.SaveChangesAsync();
        }

        await using (AuthDbContext db = Create())
        {
            AppUser loaded = await db.Users.Include(u => u.UserRoles).ThenInclude(ur => ur.Role).SingleAsync(u => u.NormalizedEmail == "ADA@TEST.LOCAL");

            Assert.Equal("Ada Lovelace", loaded.FullName);
            Assert.Equal("Admin", Assert.Single(loaded.UserRoles).Role!.Name);
            Assert.Equal(user.PasskeyUserHandle, loaded.PasskeyUserHandle);

            UserPasskey<Guid> passkey = await db.Passkeys.SingleAsync(p => p.UserId == user.Id);
            Assert.Equal(uint.MaxValue, passkey.SignCount);
            Assert.True(await db.RefreshTokens.AnyAsync(t => t.TokenHash == "hash-1"));
        }
    }

    [Theory]
    [InlineData("email")]
    [InlineData("token")]
    [InlineData("role")]
    public async Task Unique_indexes_are_enforced(string kind)
    {
        await using AuthDbContext db = Create();
        var first = new AppUser("ada@test.local", "Ada");
        db.Users.Add(first);
        await db.SaveChangesAsync();

        switch (kind)
        {
            case "email":
                db.Users.Add(new AppUser("ADA@test.local", "Kopya"));
                break;
            case "token":
                db.RefreshTokens.Add(new RefreshToken<Guid>(first.Id, Token("same"), DateTimeOffset.UtcNow, null));
                db.RefreshTokens.Add(new RefreshToken<Guid>(first.Id, Token("same"), DateTimeOffset.UtcNow, null));
                break;
            case "role":
                db.Roles.Add(new Role<Guid>(Guid.CreateVersion7(), "Admin"));
                db.Roles.Add(new Role<Guid>(Guid.CreateVersion7(), "admin"));
                break;
        }

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Permanently_deleting_a_user_deletes_its_security_records()
    {
        var user = new AppUser("ada@test.local", "Ada");
        await using (AuthDbContext db = Create())
        {
            db.Users.Add(user);
            db.RefreshTokens.Add(new RefreshToken<Guid>(user.Id, Token("h"), DateTimeOffset.UtcNow, null));
            await db.SaveChangesAsync();
        }

        await using (AuthDbContext db = Create())
        {
            db.Users.Remove(await db.Users.SingleAsync());
            await db.SaveChangesAsync();
            Assert.False(await db.RefreshTokens.AnyAsync());
        }
    }

    [Fact]
    public void Table_prefix_and_schema_are_applied()
    {
        using var db = new PrefixedAuthDbContext(new DbContextOptionsBuilder<PrefixedAuthDbContext>().UseSqlite(_connection).Options);
        Assert.Equal("AuthUsers", db.Model.FindEntityType(typeof(AppUser))!.GetTableName());
        Assert.Equal("AuthRefreshTokens", db.Model.FindEntityType(typeof(RefreshToken<Guid>))!.GetTableName());
        Assert.Equal("identity", db.Model.FindEntityType(typeof(AppUser))!.GetSchema());
    }
}
