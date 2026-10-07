using Can.Core.Mediator.DependencyInjection;
using Can.Core.MultiTenancy;
using Can.Core.Persistence.DependencyInjection;
using Can.Core.Persistence.MultiTenancy;
using Can.Core.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Persistence.Tests;

/// <summary>
/// Karma kurulum: iki tenant'ın kendi veritabanı var, üçüncüsü ortak (host) veritabanını kullanıyor.
/// Her veritabanı bellekte, paylaşımlı önbellekli ayrı bir SQLite veritabanıdır.
/// </summary>
public sealed class TenantDatabaseTests : IAsyncLifetime
{
    private readonly string _hostDb = $"Data Source=host-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly TenantInfo _tenantA;
    private readonly TenantInfo _tenantB;
    private readonly TenantInfo _sharedTenant;
    private readonly List<SqliteConnection> _keepAlive = [];
    private ServiceProvider _provider = null!;

    public TenantDatabaseTests()
    {
        _tenantA = new TenantInfo
        {
            Id = Guid.NewGuid().ToString(),
            Identifier = "a",
            ConnectionString = $"Data Source=tenant-a-{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
        };
        _tenantB = new TenantInfo
        {
            Id = Guid.NewGuid().ToString(),
            Identifier = "b",
            ConnectionString = $"Data Source=tenant-b-{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
        };
        _sharedTenant = new TenantInfo { Id = Guid.NewGuid().ToString(), Identifier = "shared" };
    }

    public async ValueTask InitializeAsync()
    {
        // Bellekteki paylaşımlı SQLite veritabanı, en az bir bağlantı açık kaldıkça yaşar.
        foreach (string connectionString in new[] { _hostDb, _tenantA.ConnectionString!, _tenantB.ConnectionString! })
        {
            var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            _keepAlive.Add(connection);
        }

        var services = new ServiceCollection();
        services.AddSingleton(new EventLog());
        services.AddCanMediator(cfg => cfg.RegisterServicesFromAssemblyContaining<TenantDatabaseTests>());
        services.AddCanMultiTenancy(o =>
        {
            o.DefaultConnectionString = _hostDb;
            o.Tenants = [_tenantA, _tenantB, _sharedTenant];
        });
        services.AddCanPersistence<TestDbContext>((sp, options) =>
            options.UseSqlite(sp.GetRequiredService<ITenantConnectionStringResolver>().Resolve())
        );

        _provider = services.BuildServiceProvider();

        await _provider.InitializeTenantDatabasesAsync<TestDbContext>((db, ct) => db.Database.EnsureCreatedAsync(ct));
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        foreach (SqliteConnection connection in _keepAlive)
            await connection.DisposeAsync();
    }

    private async Task AddProductAsync(TenantInfo tenant, string name)
    {
        await using AsyncServiceScope scope = _provider.CreateTenantScope(tenant);
        await scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>().AddAsync(Product.Create(name, 1));
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
    }

    private async Task<string[]> ListAsync(TenantInfo tenant)
    {
        await using AsyncServiceScope scope = _provider.CreateTenantScope(tenant);
        return await scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>().Query().Select(p => p.Name).ToArrayAsync();
    }

    private long CountRows(string connectionString)
    {
        SqliteConnection connection = _keepAlive.Single(c => c.ConnectionString == connectionString);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Products";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public async Task Each_tenant_writes_to_its_own_database()
    {
        await AddProductAsync(_tenantA, "A ürünü");
        await AddProductAsync(_tenantB, "B ürünü");
        await AddProductAsync(_sharedTenant, "Ortak ürün");

        Assert.Equal(1, CountRows(_tenantA.ConnectionString!));
        Assert.Equal(1, CountRows(_tenantB.ConnectionString!));
        Assert.Equal(1, CountRows(_hostDb));

        Assert.Equal(new[] { "A ürünü" }, await ListAsync(_tenantA));
        Assert.Equal(new[] { "B ürünü" }, await ListAsync(_tenantB));
        Assert.Equal(new[] { "Ortak ürün" }, await ListAsync(_sharedTenant));
    }

    [Fact]
    public async Task Tenants_sharing_the_host_database_are_still_isolated_by_filter()
    {
        var anotherShared = new TenantInfo { Id = Guid.NewGuid().ToString(), Identifier = "shared-2" };

        await AddProductAsync(_sharedTenant, "Bir");
        await AddProductAsync(anotherShared, "İki");

        Assert.Equal(2, CountRows(_hostDb));
        Assert.Equal(new[] { "Bir" }, await ListAsync(_sharedTenant));
        Assert.Equal(new[] { "İki" }, await ListAsync(anotherShared));
    }
}
