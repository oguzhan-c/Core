using Can.Core.Application;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.MultiTenancy.Tests;

public class MultiTenancyTests
{
    private static readonly TenantInfo Shared = new() { Id = Guid.NewGuid().ToString(), Identifier = "shared", Name = "Ortak" };

    private static readonly TenantInfo Dedicated = new()
    {
        Id = Guid.NewGuid().ToString(),
        Identifier = "acme",
        Name = "Acme",
        ConnectionString = "Data Source=acme",
    };

    private static ServiceProvider Build() =>
        new ServiceCollection()
            .AddCanMultiTenancy(o =>
            {
                o.DefaultConnectionString = "Data Source=host";
                o.Tenants = [Shared, Dedicated];
            })
            .BuildServiceProvider();

    [Fact]
    public async Task Store_finds_tenant_by_id_or_identifier_case_insensitively()
    {
        var store = new InMemoryTenantStore([Shared, Dedicated]);

        Assert.Same(Dedicated, await store.FindAsync("ACME"));
        Assert.Same(Dedicated, await store.FindAsync(Dedicated.Id.ToUpperInvariant()));
        Assert.Null(await store.FindAsync("yok"));
        Assert.Equal(2, (await store.GetAllAsync()).Count);
    }

    [Fact]
    public void Store_rejects_duplicate_keys()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new InMemoryTenantStore([new TenantInfo { Id = "1", Identifier = "a" }, new TenantInfo { Id = "2", Identifier = "A" }])
        );
    }

    [Fact]
    public async Task Connection_string_follows_the_active_tenant()
    {
        await using ServiceProvider provider = Build();

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            Assert.Equal("Data Source=host", scope.ServiceProvider.GetRequiredService<ITenantConnectionStringResolver>().Resolve());

        await using (AsyncServiceScope scope = provider.CreateTenantScope(Shared))
            Assert.Equal("Data Source=host", scope.ServiceProvider.GetRequiredService<ITenantConnectionStringResolver>().Resolve());

        await using (AsyncServiceScope scope = provider.CreateTenantScope(Dedicated))
            Assert.Equal("Data Source=acme", scope.ServiceProvider.GetRequiredService<ITenantConnectionStringResolver>().Resolve());
    }

    [Fact]
    public async Task Current_tenant_id_is_parsed_to_entity_type()
    {
        await using ServiceProvider provider = Build();

        await using (AsyncServiceScope scope = provider.CreateTenantScope(Dedicated))
            Assert.Equal(Guid.Parse(Dedicated.Id), scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Id);

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
            Assert.Null(scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Id);
    }

    [Fact]
    public async Task Scopes_do_not_share_tenants()
    {
        await using ServiceProvider provider = Build();

        await using AsyncServiceScope first = provider.CreateTenantScope(Dedicated);
        await using AsyncServiceScope second = provider.CreateAsyncScope();

        Assert.NotNull(first.ServiceProvider.GetRequiredService<ICurrentTenant>().Id);
        Assert.Null(second.ServiceProvider.GetRequiredService<ICurrentTenant>().Id);
    }

    [Fact]
    public async Task Missing_connection_string_is_an_error_not_a_silent_fallback()
    {
        await using ServiceProvider provider = new ServiceCollection().AddCanMultiTenancy().BuildServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        Assert.Throws<InvalidOperationException>(() =>
            scope.ServiceProvider.GetRequiredService<ITenantConnectionStringResolver>().Resolve()
        );
    }

    [Fact]
    public void Last_configuration_wins_and_replaces_null_tenant()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentTenant>(NullCurrentTenant.Instance);
        services.AddCanMultiTenancy(o => o.DefaultConnectionString = "ilk");
        services.AddCanMultiTenancy(o => o.DefaultConnectionString = "son");

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal("son", provider.GetRequiredService<MultiTenancyOptions>().DefaultConnectionString);
        Assert.Single(services, d => d.ServiceType == typeof(ICurrentTenant));
    }

    [Fact]
    public async Task Custom_id_parser_supports_int_tenant_ids()
    {
        await using ServiceProvider provider = new ServiceCollection()
            .AddCanMultiTenancy(o => o.TenantIdParser = v => int.TryParse(v, out int id) ? id : null)
            .BuildServiceProvider();

        await using AsyncServiceScope scope = provider.CreateTenantScope(new TenantInfo { Id = "42" });

        Assert.Equal(42, scope.ServiceProvider.GetRequiredService<ICurrentTenant>().Id);
    }
}
