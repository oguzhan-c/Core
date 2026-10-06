using Can.Core.Application.DependencyInjection;
using Can.Core.Application.Exceptions;
using Can.Core.Domain.Exceptions;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Application.Tests;

public class ApplicationTests
{
    private sealed class Host
    {
        public FakeCurrentUser User { get; } = new();
        public FakeCurrentTenant Tenant { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public Counter Counter { get; } = new();
        public ServiceProvider Provider { get; }

        public Host()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICurrentUser>(User);
            services.AddSingleton<ICurrentTenant>(Tenant);
            services.AddSingleton<IUnitOfWork>(UnitOfWork);
            services.AddSingleton(Counter);

            services.AddCanApplication(typeof(ApplicationTests).Assembly);

            Provider = services.BuildServiceProvider();
        }

        public ISender Sender => Provider.GetRequiredService<ISender>();
    }

    private static readonly CreateProductCommand ValidCommand = new("Kalem", 10);

    // ---------------------------------------------------------------- yetkilendirme

    [Fact]
    public async Task Unauthenticated_user_is_rejected_before_validation()
    {
        var host = new Host();
        host.User.Id = null;

        // geçersiz istek olmasına rağmen önce kimlik kontrolü çalışır
        await Assert.ThrowsAsync<UnauthorizedException>(() => host.Sender.Send(new CreateProductCommand("", 0)));
    }

    [Fact]
    public async Task User_without_required_role_is_forbidden()
    {
        var host = new Host();
        host.User.RoleList.Add("Product.Read");

        await Assert.ThrowsAsync<ForbiddenException>(() => host.Sender.Send(ValidCommand));
        Assert.Equal(0, host.Counter.Value);
    }

    [Fact]
    public async Task Required_role_or_admin_role_is_allowed()
    {
        var host = new Host();

        host.User.RoleList.Add("product.write"); // büyük/küçük harf duyarsız
        Assert.Equal(1, await host.Sender.Send(ValidCommand));

        host.User.RoleList.Clear();
        host.User.RoleList.Add("Admin");
        Assert.Equal(2, await host.Sender.Send(ValidCommand));
    }

    [Fact]
    public async Task Required_permission_or_wildcard_is_allowed()
    {
        var host = new Host();

        host.User.PermissionList.Add("orders.*");
        await Assert.ThrowsAsync<ForbiddenException>(() => host.Sender.Send(ValidCommand));

        host.User.PermissionList.Add("PRODUCTS.CREATE");
        Assert.Equal(1, await host.Sender.Send(ValidCommand));

        host.User.PermissionList.Clear();
        host.User.PermissionList.Add("products.*");
        Assert.Equal(2, await host.Sender.Send(ValidCommand));
    }

    [Theory]
    [InlineData("*", "products.write", true)]
    [InlineData("products.*", "products.write", true)]
    [InlineData("products.*", "products", true)]
    [InlineData("products.*", "products.variants.write", true)]
    [InlineData("products.*", "productsx.write", false)]
    [InlineData("products.write", "products.read", false)]
    [InlineData("Products.Write", "products.write", true)]
    public void Permission_matching(string granted, string required, bool expected)
    {
        Assert.Equal(expected, PermissionMatcher.Covers(granted, required));
    }

    // ---------------------------------------------------------------- doğrulama

    [Fact]
    public async Task Invalid_request_returns_field_errors()
    {
        var host = new Host();
        host.User.RoleList.Add("Admin");

        var ex = await Assert.ThrowsAsync<ValidationException>(() => host.Sender.Send(new CreateProductCommand("", 0)));

        Assert.Contains("Name", ex.Errors.Keys);
        Assert.Contains("Price", ex.Errors.Keys);
        Assert.Equal(0, host.Counter.Value);
    }

    // ---------------------------------------------------------------- transaction

    [Fact]
    public async Task Transactional_request_runs_inside_unit_of_work()
    {
        var host = new Host();
        host.User.RoleList.Add("Admin");

        await host.Sender.Send(ValidCommand);

        Assert.Equal(1, host.UnitOfWork.TransactionCount);
        Assert.False(host.UnitOfWork.RolledBack);
    }

    [Fact]
    public async Task Failing_transactional_request_is_rolled_back()
    {
        var host = new Host();

        await Assert.ThrowsAsync<BusinessException>(() => host.Sender.Send(new FailingCommand()));

        Assert.True(host.UnitOfWork.RolledBack);
    }

    [Fact]
    public async Task Non_transactional_request_does_not_open_transaction()
    {
        var host = new Host();

        await host.Sender.Send(new GetProductsQuery(1));

        Assert.Equal(0, host.UnitOfWork.TransactionCount);
    }

    // ---------------------------------------------------------------- önbellek

    [Fact]
    public async Task Cachable_query_is_served_from_cache_until_tag_is_removed()
    {
        var host = new Host();
        host.User.RoleList.Add("Admin");

        List<string> first = await host.Sender.Send(new GetProductsQuery(1));
        List<string> second = await host.Sender.Send(new GetProductsQuery(1));
        Assert.Equal(first, second);
        Assert.Equal(1, host.Counter.Value);

        await host.Sender.Send(new GetProductsQuery(2)); // farklı anahtar
        Assert.Equal(2, host.Counter.Value);

        await host.Sender.Send(ValidCommand); // "products" etiketini siler (sayaç 3)
        List<string> afterRemoval = await host.Sender.Send(new GetProductsQuery(1));

        Assert.Equal(new[] { "call-4" }, afterRemoval);
    }

    [Fact]
    public async Task Cache_is_isolated_per_tenant()
    {
        var host = new Host();

        host.Tenant.Id = Guid.NewGuid();
        List<string> tenantA = await host.Sender.Send(new GetProductsQuery(1));

        host.Tenant.Id = Guid.NewGuid();
        List<string> tenantB = await host.Sender.Send(new GetProductsQuery(1));

        Assert.NotEqual(tenantA, tenantB); // B, A'nın önbelleğini görmedi
        Assert.Equal(2, host.Counter.Value);
    }

    [Fact]
    public async Task BypassCache_always_runs_handler()
    {
        var host = new Host();

        await host.Sender.Send(new GetProductsQuery(1, Fresh: true));
        await host.Sender.Send(new GetProductsQuery(1, Fresh: true));

        Assert.Equal(2, host.Counter.Value);
    }

    // ---------------------------------------------------------------- kayıt

    [Fact]
    public void Business_rules_are_registered()
    {
        var host = new Host();
        using IServiceScope scope = host.Provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetService<ProductBusinessRules>());
    }

    [Fact]
    public void Defaults_are_registered_when_missing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCanApplication(typeof(ApplicationTests).Assembly);

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(NullCurrentUser.Instance, provider.GetRequiredService<ICurrentUser>());
        Assert.NotNull(provider.GetRequiredService<CanApplicationOptions>());
    }
}
