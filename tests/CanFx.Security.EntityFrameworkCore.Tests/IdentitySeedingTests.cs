using CanFx.Security.Entities;
using CanFx.Security.Hashing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.Security.EntityFrameworkCore.Tests;

public sealed class IdentitySeedingTests : IAsyncLifetime
{
    private static readonly (string Name, string Description)[] Catalog =
    [
        ("products.read", "Ürünleri görme"),
        ("products.write", "Ürün ekleme / düzenleme"),
        ("orders.cancel", "Sipariş iptali"),
    ];

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _services = null!;
    private string? _adminPassword = "Admin123!";
    private (string Name, string Description)[] _catalog = Catalog;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        _services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IPasswordHasher>(new Pbkdf2PasswordHasher(10_000))
            .AddCanIdentitySeed<AppUser, Guid>(o =>
            {
                o.Permissions = _catalog;   // testler kataloğu değiştirebilsin diye alan üzerinden
                o.Roles = ["Admin", "Editor"];
                o.RoleGrants = new Dictionary<string, string[]>
                {
                    ["Admin"] = ["products.read", "products.write", "orders.cancel"],
                    ["Editor"] = ["products.read", "products.write"],
                };
                o.NewId = Guid.CreateVersion7;
                o.AdminEmail = "admin@test.local";
                o.AdminPassword = _adminPassword;
                o.CreateAdmin = (email, hash) =>
                {
                    var user = new AppUser(email, "Platform Yöneticisi");
                    user.SetPasswordHash(hash);
                    return user;
                };
            })
            .AddDbContext<AuthDbContext>((sp, o) => o.UseSqlite(_connection).UseCanIdentitySeeding<AppUser, Guid>(sp))
            .BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task EnsureCreatedAsync()
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.EnsureCreatedAsync();
    }

    private async Task<T> QueryAsync<T>(Func<AuthDbContext, Task<T>> query)
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<AuthDbContext>());
    }

    [Fact]
    public async Task Ensure_created_runs_the_seed_hook()
    {
        await EnsureCreatedAsync();

        Assert.Equal(3, await QueryAsync(db => db.OperationClaims.CountAsync()));
        Role<Guid> admin = await QueryAsync(db => db.Roles.Include(r => r.OperationClaims).SingleAsync(r => r.Name == "Admin"));
        Assert.True(admin.IsSystem);
        Assert.Equal(3, admin.OperationClaims.Count);

        AppUser user = await QueryAsync(db => db.Users.Include(u => u.UserRoles).SingleAsync());
        Assert.Equal("ADMIN@TEST.LOCAL", user.NormalizedEmail);
        Assert.Single(user.UserRoles);
        Assert.Equal(PasswordVerificationResult.Success, new Pbkdf2PasswordHasher(10_000).Verify("Admin123!", user.PasswordHash!));
    }

    [Fact]
    public async Task Running_again_adds_nothing_and_keeps_revoked_grants_revoked()
    {
        await EnsureCreatedAsync();

        // Yönetim ekranından Editor'ın yazma yetkisi geri alındı.
        await using (AsyncServiceScope scope = _services.CreateAsyncScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Role<Guid> editor = await db.Roles.Include(r => r.OperationClaims).SingleAsync(r => r.Name == "Editor");
            OperationClaim<Guid> write = await db.OperationClaims.SingleAsync(c => c.Name == "products.write");
            editor.OperationClaims.Remove(editor.OperationClaims.Single(c => c.OperationClaimId == write.Id));
            await db.SaveChangesAsync();
        }

        await _services.SeedIdentityAsync<AuthDbContext, AppUser, Guid>();

        Assert.Equal(3, await QueryAsync(db => db.OperationClaims.CountAsync()));
        Assert.Equal(2, await QueryAsync(db => db.Roles.CountAsync()));
        Assert.Equal(1, await QueryAsync(db => db.Users.CountAsync()));
        Role<Guid> editorAfter = await QueryAsync(db => db.Roles.Include(r => r.OperationClaims).SingleAsync(r => r.Name == "Editor"));
        Assert.Single(editorAfter.OperationClaims);
    }

    [Fact]
    public async Task New_permission_is_granted_and_removed_one_is_deprecated()
    {
        await EnsureCreatedAsync();

        _catalog = [("products.read", "Ürünleri görme"), ("products.write", "Ürün ekleme / düzenleme"), ("reports.read", "Raporlar")];
        _services.GetRequiredService<IdentitySeedOptions<AppUser, Guid>>().Permissions = _catalog;
        _services.GetRequiredService<IdentitySeedOptions<AppUser, Guid>>().RoleGrants = new Dictionary<string, string[]>
        {
            ["Admin"] = ["products.read", "products.write", "reports.read"],
        };

        await _services.SeedIdentityAsync<AuthDbContext, AppUser, Guid>();

        OperationClaim<Guid> cancel = await QueryAsync(db => db.OperationClaims.SingleAsync(c => c.Name == "orders.cancel"));
        Assert.True(cancel.IsDeprecated);
        Role<Guid> admin = await QueryAsync(db => db.Roles.Include(r => r.OperationClaims).SingleAsync(r => r.Name == "Admin"));
        OperationClaim<Guid> reports = await QueryAsync(db => db.OperationClaims.SingleAsync(c => c.Name == "reports.read"));
        Assert.Contains(admin.OperationClaims, c => c.OperationClaimId == reports.Id);
    }

    [Fact]
    public async Task Without_password_no_admin_is_created()
    {
        _adminPassword = null;
        _services.GetRequiredService<IdentitySeedOptions<AppUser, Guid>>().AdminPassword = null;

        await EnsureCreatedAsync();

        Assert.Equal(0, await QueryAsync(db => db.Users.CountAsync()));
        Assert.Equal(2, await QueryAsync(db => db.Roles.CountAsync()));
    }

    [Fact]
    public async Task Run_when_false_skips_the_seed()
    {
        _services.GetRequiredService<IdentitySeedOptions<AppUser, Guid>>().RunWhen = _ => false;

        await EnsureCreatedAsync();

        Assert.Equal(0, await QueryAsync(db => db.Roles.CountAsync()));
    }

    [Fact]
    public async Task Unknown_grant_fails_with_a_clear_message()
    {
        _services.GetRequiredService<IdentitySeedOptions<AppUser, Guid>>().RoleGrants = new Dictionary<string, string[]>
        {
            ["Editor"] = ["no.such.permission"],
        };

        Exception error = await Assert.ThrowsAnyAsync<Exception>(EnsureCreatedAsync);
        Assert.Contains("no.such.permission", error.ToString(), StringComparison.Ordinal);
    }
}
