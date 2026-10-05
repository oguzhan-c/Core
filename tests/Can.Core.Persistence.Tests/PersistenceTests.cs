using Can.Core.Persistence.Context;
using Can.Core.Persistence.Dynamic;
using Can.Core.Persistence.Paging;
using Can.Core.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Persistence.Tests;

public class PersistenceTests
{
    // ---------------------------------------------------------------- yardımcılar

    private static async Task<int> AddProductAsync(TestHost host, string name = "Kalem", int price = 10)
    {
        await using AsyncServiceScope scope = host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        Product product = await repository.AddAsync(Product.Create(name, price));
        await unitOfWork.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<Product?> FindAsync(TestHost host, int id, bool withDeleted = false)
    {
        await using AsyncServiceScope scope = host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();
        return await repository.GetByIdAsync(id, withDeleted: withDeleted);
    }

    // ---------------------------------------------------------------- audit

    [Fact]
    public async Task Add_fills_creation_audit_and_tenant()
    {
        await using TestHost host = await TestHost.CreateAsync();

        int id = await AddProductAsync(host);
        Product product = (await FindAsync(host, id))!;

        Assert.Equal(host.Clock.Now, product.CreatedAt);
        Assert.Equal("user-1", product.CreatedBy);
        Assert.Equal(FakeCurrentTenant.TenantA, product.TenantId);
        Assert.Null(product.UpdatedAt);
    }

    [Fact]
    public async Task Update_fills_modification_audit_and_keeps_creation_info()
    {
        await using TestHost host = await TestHost.CreateAsync();
        int id = await AddProductAsync(host);
        DateTimeOffset createdAt = host.Clock.Now;

        host.Clock.Now = createdAt.AddHours(1);
        host.User.Id = "user-2";

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();
            Product product = (await repository.GetByIdAsync(id))!;
            product.ChangePrice(99);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        Product updated = (await FindAsync(host, id))!;
        Assert.Equal(99, updated.Price);
        Assert.Equal(createdAt, updated.CreatedAt);
        Assert.Equal("user-1", updated.CreatedBy);
        Assert.Equal(createdAt.AddHours(1), updated.UpdatedAt);
        Assert.Equal("user-2", updated.UpdatedBy);
    }

    // ---------------------------------------------------------------- soft delete

    [Fact]
    public async Task Repository_delete_is_soft_and_hidden_by_default()
    {
        await using TestHost host = await TestHost.CreateAsync();
        int id = await AddProductAsync(host);

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();
            repository.Delete((await repository.GetByIdAsync(id))!);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        Assert.Null(await FindAsync(host, id));

        Product deleted = (await FindAsync(host, id, withDeleted: true))!;
        Assert.True(deleted.IsDeleted);
        Assert.Equal(host.Clock.Now, deleted.DeletedAt);
        Assert.Equal("user-1", deleted.DeletedBy);
    }

    [Fact]
    public async Task DbContext_remove_is_converted_to_soft_delete()
    {
        await using TestHost host = await TestHost.CreateAsync();
        int id = await AddProductAsync(host);

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
            db.Products.Remove(await db.Products.SingleAsync(p => p.Id == id));
            await db.SaveChangesAsync();
        }

        Product? deleted = await FindAsync(host, id, withDeleted: true);
        Assert.NotNull(deleted);
        Assert.True(deleted.IsDeleted);
    }

    [Fact]
    public async Task Permanent_delete_removes_the_row()
    {
        await using TestHost host = await TestHost.CreateAsync();
        int id = await AddProductAsync(host);

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();
            repository.Delete((await repository.GetByIdAsync(id))!, permanent: true);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        Assert.Null(await FindAsync(host, id, withDeleted: true));
    }

    // ---------------------------------------------------------------- tenant

    [Fact]
    public async Task Tenant_filter_isolates_tenants_even_with_deleted_records()
    {
        await using TestHost host = await TestHost.CreateAsync();
        int id = await AddProductAsync(host);

        host.Tenant.Id = FakeCurrentTenant.TenantB;
        Assert.Null(await FindAsync(host, id));
        Assert.Null(await FindAsync(host, id, withDeleted: true));

        host.Tenant.Id = null; // tenant yoksa hiçbir tenant'ın verisi görünmez
        Assert.Null(await FindAsync(host, id));

        host.Tenant.Id = FakeCurrentTenant.TenantA;
        Assert.NotNull(await FindAsync(host, id));
    }

    [Fact]
    public async Task Writing_to_another_tenant_is_rejected()
    {
        await using TestHost host = await TestHost.CreateAsync();

        await using AsyncServiceScope scope = host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();

        Product product = Product.Create("Yabancı", 1);
        product.TenantId = FakeCurrentTenant.TenantB;
        await repository.AddAsync(product);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync()
        );
    }

    // ---------------------------------------------------------------- domain events

    [Fact]
    public async Task Domain_events_are_published_after_save_and_cleared()
    {
        await using TestHost host = await TestHost.CreateAsync();

        await using AsyncServiceScope scope = host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();

        Product product = await repository.AddAsync(Product.Create("Kalem", 10));
        Assert.Empty(host.Events.Entries); // kaydetmeden önce yayınlanmaz

        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        Assert.Equal(new[] { "created:Kalem" }, host.Events.Entries);
        Assert.Empty(product.DomainEvents);

        product.ChangePrice(25);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        Assert.Equal(new[] { "created:Kalem", "price:25" }, host.Events.Entries);
    }

    // ---------------------------------------------------------------- transaction

    [Fact]
    public async Task Rollback_discards_saved_changes()
    {
        await using TestHost host = await TestHost.CreateAsync();

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await unitOfWork.BeginTransactionAsync();
            await repository.AddAsync(Product.Create("Geçici", 1));
            await unitOfWork.SaveChangesAsync();
            await unitOfWork.RollbackTransactionAsync();

            Assert.False(unitOfWork.HasActiveTransaction);
        }

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>().CountAsync());
        }
    }

    [Fact]
    public async Task ExecuteInTransaction_commits_on_success_and_rolls_back_on_error()
    {
        await using TestHost host = await TestHost.CreateAsync();

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            await unitOfWork.ExecuteInTransactionAsync(async ct =>
            {
                await repository.AddAsync(Product.Create("Kalıcı", 1), ct);
                return true;
            });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                unitOfWork.ExecuteInTransactionAsync<bool>(async ct =>
                {
                    await repository.AddAsync(Product.Create("Yarım", 1), ct);
                    await unitOfWork.SaveChangesAsync(ct);
                    throw new InvalidOperationException("iş kuralı");
                })
            );
        }

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();
            Assert.Equal(1, await repository.CountAsync());
            Assert.True(await repository.AnyAsync(p => p.Name == "Kalıcı"));
        }
    }

    // ---------------------------------------------------------------- liste, dinamik sorgu, sayfalama

    private static async Task SeedCatalogAsync(TestHost host)
    {
        await using AsyncServiceScope scope = host.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TestDbContext>();

        var office = new Category("Ofis");
        var school = new Category("Okul");

        db.Products.AddRange(
            Product.Create("Kirmizi Kalem", 10, office),
            Product.Create("Mavi Kalem", 20, office),
            Product.Create("Defter", 30, school),
            Product.Create("Silgi", 5)
        );

        await db.SaveChangesAsync();
    }

    private static async Task<IPaginate<Product>> QueryAsync(TestHost host, DynamicQuery query, int index = 0, int size = 10)
    {
        await using AsyncServiceScope scope = host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();
        return await repository.GetListByDynamicAsync(query, index: index, size: size, enableTracking: false);
    }

    private static string[] Names(IPaginate<Product> page) => page.Items.Select(p => p.Name).ToArray();

    [Fact]
    public async Task Dynamic_contains_is_case_insensitive_by_default()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await SeedCatalogAsync(host);

        var page = await QueryAsync(host, new DynamicQuery { Filter = new Filter("name", "contains", "KALEM") });

        Assert.Equal(new[] { "Kirmizi Kalem", "Mavi Kalem" }, Names(page));
    }

    [Fact]
    public async Task Dynamic_in_between_and_isnull()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await SeedCatalogAsync(host);

        Assert.Equal(new[] { "Kirmizi Kalem", "Defter" }, Names(await QueryAsync(host, new() { Filter = new Filter("price", "in", "10, 30") })));
        Assert.Equal(new[] { "Kirmizi Kalem", "Mavi Kalem" }, Names(await QueryAsync(host, new() { Filter = new Filter("price", "between", "6,25") })));
        Assert.Equal(new[] { "Silgi" }, Names(await QueryAsync(host, new() { Filter = new Filter("category", "isnull") })));
    }

    [Fact]
    public async Task Dynamic_nested_or_over_navigation_with_sort()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await SeedCatalogAsync(host);

        var query = new DynamicQuery
        {
            Filter = new Filter("name", "eq", "Defter")
            {
                Logic = "or",
                Filters = [new Filter("category.name", "eq", "Ofis")],
            },
            Sort = [new Sort("price", "desc")],
        };

        Assert.Equal(new[] { "Defter", "Mavi Kalem", "Kirmizi Kalem" }, Names(await QueryAsync(host, query)));
    }

    [Fact]
    public async Task Dynamic_rejects_unknown_fields_and_operators()
    {
        await using TestHost host = await TestHost.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => QueryAsync(host, new() { Filter = new Filter("password", "eq", "x") }));
        await Assert.ThrowsAsync<ArgumentException>(() => QueryAsync(host, new() { Filter = new Filter("name", "like", "x") }));
        await Assert.ThrowsAsync<ArgumentException>(() => QueryAsync(host, new() { Sort = [new Sort("name", "sideways")] }));
    }

    [Fact]
    public async Task Paging_returns_requested_page_and_metadata()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await SeedCatalogAsync(host);

        await using AsyncServiceScope scope = host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();

        IPaginate<Product> page = await repository.GetListAsync(orderBy: q => q.OrderBy(p => p.Price), index: 1, size: 3);

        Assert.Equal(4, page.Count);
        Assert.Equal(2, page.Pages);
        Assert.True(page.HasPrevious);
        Assert.False(page.HasNext);
        Assert.Equal("Defter", Assert.Single(page.Items).Name);

        IPaginate<string> names = page.Map(p => p.Name);
        Assert.Equal(1, names.Index);
        Assert.Equal(new[] { "Defter" }, names.Items);
    }

    [Fact]
    public async Task Include_loads_navigation()
    {
        await using TestHost host = await TestHost.CreateAsync();
        await SeedCatalogAsync(host);

        await using AsyncServiceScope scope = host.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();

        Product? product = await repository.GetAsync(p => p.Name == "Defter", include: q => q.Include(p => p.Category));

        Assert.Equal("Okul", product?.Category?.Name);
    }

    [Fact]
    public void Dynamic_query_also_works_in_memory_with_null_navigation()
    {
        IQueryable<Product> products = new[]
        {
            Product.Create("A", 1, new Category("Ofis")),
            Product.Create("B", 2),
        }.AsQueryable();

        List<Product> result = products.ToDynamic(new DynamicQuery { Filter = new Filter("category.name", "eq", "Ofis") }).ToList();

        Assert.Equal("A", Assert.Single(result).Name);
    }

    [Fact]
    public void Named_filter_constants_are_stable()
    {
        Assert.Equal("Can.SoftDelete", CanQueryFilters.SoftDelete);
        Assert.Equal("Can.Tenant", CanQueryFilters.Tenant);
    }
}
