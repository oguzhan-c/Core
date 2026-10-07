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
    public DbSet<OperationClaim<Guid>> OperationClaims => Set<OperationClaim<Guid>>();
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

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        await using AuthDbContext db = Create();
        await db.Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private AuthDbContext Create() => new(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(_connection).Options);

    private static RefreshTokenValue Token(string hash) => new("raw-" + hash, hash, DateTimeOffset.UtcNow.AddDays(7));

    [Fact]
    public async Task Permissions_come_from_roles_and_direct_grants()
    {
        var write = new OperationClaim<Guid>(Guid.CreateVersion7(), "Products.Write", "Ürün ekleme/düzenleme");
        var read = new OperationClaim<Guid>(Guid.CreateVersion7(), "products.read");
        var cancel = new OperationClaim<Guid>(Guid.CreateVersion7(), "orders.cancel");

        var editor = new Role<Guid>(Guid.CreateVersion7(), "Editor");
        editor.GrantOperationClaim(write);
        editor.GrantOperationClaim(read);
        editor.GrantOperationClaim(read); // tekrar eklenmez

        var user = new AppUser("grace@test.local", "Grace Hopper");
        user.AddRole(editor);
        user.GrantOperationClaim(cancel);
        user.GrantOperationClaim(read); // rolde de var; ad bir kez döner

        await using (AuthDbContext db = Create())
        {
            db.OperationClaims.AddRange(write, read, cancel);
            db.Roles.Add(editor);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        await using (AuthDbContext db = Create())
        {
            AppUser loaded = await db.Users
                .Include(u => u.UserRoles).ThenInclude(ur => ur.Role!).ThenInclude(r => r.OperationClaims).ThenInclude(rc => rc.OperationClaim)
                .Include(u => u.OperationClaims).ThenInclude(uc => uc.OperationClaim)
                .SingleAsync(u => u.Id == user.Id);

            Assert.Equal(new[] { "orders.cancel", "products.read", "products.write" }, loaded.GetPermissionNames());

            Role<Guid> role = await db.Roles.Include(r => r.OperationClaims).SingleAsync(r => r.Id == editor.Id);
            role.RevokeOperationClaim(write.Id);
            await db.SaveChangesAsync();
        }

        await using (AuthDbContext db = Create())
        {
            Assert.Single(await db.Roles.Where(r => r.Id == editor.Id).SelectMany(r => r.OperationClaims).ToListAsync());
            await Assert.ThrowsAsync<DbUpdateException>(async () =>
            {
                db.OperationClaims.Add(new OperationClaim<Guid>(Guid.CreateVersion7(), "PRODUCTS.WRITE"));
                await db.SaveChangesAsync();
            });
        }
    }

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
    public async Task External_logins_round_trip_and_unlink_deletes_the_row()
    {
        var user = new AppUser("ada@test.local", "Ada Lovelace");
        string stamp = user.SecurityStamp;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.True(user.AddLogin("Google", "g-123", "Google", now));
        Assert.False(user.AddLogin("google", "g-456", "Google", now)); // sağlayıcı başına tek hesap
        Assert.True(user.AddLogin("github", "42", "GitHub", now));
        Assert.NotEqual(stamp, user.SecurityStamp); // bağlama eski oturumları geçersiz kılabilsin

        await using (AuthDbContext db = Create())
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        await using (AuthDbContext db = Create())
        {
            AppUser found = await db.Users.Include(u => u.Logins).SingleAsync(u => u.Logins.Any(l => l.LoginProvider == "google" && l.ProviderKey == "g-123"));
            Assert.Equal(user.Id, found.Id);
            Assert.Equal(new[] { "github", "google" }, found.Logins.Select(l => l.LoginProvider).Order().ToArray());

            Assert.True(found.RemoveLogin("GOOGLE"));
            Assert.False(found.RemoveLogin("google"));
            await db.SaveChangesAsync();
        }

        await using (AuthDbContext db = Create())
        {
            Assert.Equal(1, await db.Set<UserLogin<Guid>>().CountAsync());
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
