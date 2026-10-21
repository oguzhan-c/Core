using CanFx.Security.Authorization;
using CanFx.Security.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.Security.EntityFrameworkCore.Tests;

public sealed class UserAuthorizationStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        _services = new ServiceCollection()
            .AddDbContext<AuthDbContext>(o => o.UseSqlite(_connection))
            .AddCanUserAuthorizationStore<AuthDbContext, AppUser, Guid>(o => o.CacheDuration = TimeSpan.FromMinutes(5))
            .BuildServiceProvider();

        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Resolves_roles_and_permissions_without_deprecated_and_refreshes_after_invalidate()
    {
        var read = new OperationClaim<Guid>(Guid.CreateVersion7(), "colors.read");
        var write = new OperationClaim<Guid>(Guid.CreateVersion7(), "colors.write");
        var old = new OperationClaim<Guid>(Guid.CreateVersion7(), "colors.legacy");
        var editor = new Role<Guid>(Guid.CreateVersion7(), "Editor");
        editor.GrantOperationClaim(read);
        editor.GrantOperationClaim(old);
        var user = new AppUser("ada@test.local", "Ada");
        user.AddRole(editor);
        user.GrantOperationClaim(write);

        await using (AsyncServiceScope scope = _services.CreateAsyncScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            db.AddRange(read, write, old, editor, user);
            await db.SaveChangesAsync();
            old.Deprecate();
            await db.SaveChangesAsync();
        }

        UserAuthorizationData? first = await GetAsync(user.Id.ToString());
        Assert.NotNull(first);
        Assert.Equal(new[] { "Editor" }, first.Roles);
        Assert.Equal(new[] { "colors.read", "colors.write" }, first.Permissions);

        // Rolden yetki alınır: önbellek temizlenene kadar eski değer, sonra yenisi.
        await using (AsyncServiceScope scope = _services.CreateAsyncScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Role<Guid> role = await db.Roles.Include(r => r.OperationClaims).SingleAsync(r => r.Id == editor.Id);
            role.RevokeOperationClaim(read.Id);
            await db.SaveChangesAsync();
        }

        Assert.Contains("colors.read", (await GetAsync(user.Id.ToString()))!.Permissions);

        await using (AsyncServiceScope scope = _services.CreateAsyncScope())
            scope.ServiceProvider.GetRequiredService<IUserAuthorizationStore>().InvalidateAll();

        Assert.Equal(new[] { "colors.write" }, (await GetAsync(user.Id.ToString()))!.Permissions);
    }

    [Fact]
    public async Task Unknown_user_returns_null()
    {
        Assert.Null(await GetAsync(Guid.NewGuid().ToString()));
        Assert.Null(await GetAsync("not-a-guid"));
    }

    [Fact]
    public async Task Catalog_sync_adds_updates_deprecates_and_restores()
    {
        await using (AsyncServiceScope scope = _services.CreateAsyncScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            await db.SyncOperationClaimsAsync<Guid>([("colors.read", "Okuma"), ("colors.write", "Yazma")], () => Guid.CreateVersion7());
            await db.SaveChangesAsync();
        }

        await using (AsyncServiceScope scope = _services.CreateAsyncScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Dictionary<string, OperationClaim<Guid>> claims =
                await db.SyncOperationClaimsAsync<Guid>([("Colors.Read", "Renkleri görme"), ("colors.delete", "Silme")], () => Guid.CreateVersion7());
            await db.SaveChangesAsync();

            Assert.Equal("Renkleri görme", claims["colors.read"].Description);
            Assert.True(claims["colors.write"].IsDeprecated);
            Assert.False(claims["colors.delete"].IsDeprecated);
        }

        await using (AsyncServiceScope scope = _services.CreateAsyncScope())
        {
            AuthDbContext db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            Dictionary<string, OperationClaim<Guid>> claims =
                await db.SyncOperationClaimsAsync<Guid>([("colors.write", "Yazma")], () => Guid.CreateVersion7());
            await db.SaveChangesAsync();

            Assert.False(claims["colors.write"].IsDeprecated);
            Assert.True(claims["colors.read"].IsDeprecated);
            Assert.Equal(3, await db.OperationClaims.CountAsync());
        }
    }

    private async Task<UserAuthorizationData?> GetAsync(string userId)
    {
        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUserAuthorizationStore>().GetAsync(userId);
    }
}
