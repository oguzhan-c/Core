using Can.Core.Application.DependencyInjection;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Application.Tests;

/// <summary>Result döndüren isteklerde pipeline exception fırlatmaz, başarısız sonuç döndürür.</summary>
public class ResultPipelineTests
{
    private sealed class Host
    {
        public FakeCurrentUser User { get; } = new();
        public FakeUnitOfWork UnitOfWork { get; } = new();
        public Counter Counter { get; } = new();
        public ServiceProvider Provider { get; }

        public Host()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICurrentUser>(User);
            services.AddSingleton<ICurrentTenant>(new FakeCurrentTenant());
            services.AddSingleton<IUnitOfWork>(UnitOfWork);
            services.AddSingleton(Counter);
            services.AddCanApplication(typeof(ResultPipelineTests).Assembly);
            Provider = services.BuildServiceProvider();

            User.RoleList.Add("Order.Write");
        }

        public ISender Sender => Provider.GetRequiredService<ISender>();
    }

    [Fact]
    public async Task Unauthenticated_and_forbidden_requests_return_errors()
    {
        var host = new Host();

        host.User.Id = null;
        Result<int> unauthorized = await host.Sender.Send(new CreateOrderCommand("a"));
        Assert.Equal(ErrorType.Unauthorized, unauthorized.FirstError.Type);

        host.User.Id = "user-1";
        host.User.RoleList.Clear();
        Result<int> forbidden = await host.Sender.Send(new CreateOrderCommand("a"));
        Assert.Equal(ErrorType.Forbidden, forbidden.FirstError.Type);

        Assert.Equal(0, host.Counter.Value);
    }

    [Fact]
    public async Task Validation_failures_become_field_errors()
    {
        var host = new Host();

        Result<int> result = await host.Sender.Send(new CreateOrderCommand(""));

        Error error = Assert.Single(result.Errors);
        Assert.Equal(ErrorType.Validation, error.Type);
        Assert.Equal("Name", error.Field);
        Assert.Equal(0, host.Counter.Value);
    }

    [Fact]
    public async Task Failed_result_rolls_back_the_transaction()
    {
        var host = new Host();

        Result<int> rejected = await host.Sender.Send(new CreateOrderCommand("a", Reject: true));
        Assert.Equal(CreateOrderCommandHandler.Rejected, rejected.FirstError);
        Assert.True(host.UnitOfWork.RolledBack);

        var success = new Host();
        Assert.Equal(1, (await success.Sender.Send(new CreateOrderCommand("a"))).Value);
        Assert.False(success.UnitOfWork.RolledBack);
    }

    [Fact]
    public async Task Failed_query_is_not_cached()
    {
        var host = new Host();

        Result<List<string>> first = await host.Sender.Send(new GetOrdersQuery(1, Missing: true));
        Result<List<string>> second = await host.Sender.Send(new GetOrdersQuery(1, Missing: true));

        Assert.Equal(GetOrdersQueryHandler.NotFound, first.FirstError);
        Assert.Equal(GetOrdersQueryHandler.NotFound, second.FirstError);
        Assert.Equal(2, host.Counter.Value); // ikisi de handler'a gitti
    }

    [Fact]
    public async Task Failed_command_does_not_invalidate_cache()
    {
        var host = new Host();

        Result<List<string>> cached = await host.Sender.Send(new GetOrdersQuery(1));
        await host.Sender.Send(new CreateOrderCommand("a", Reject: true));
        Result<List<string>> again = await host.Sender.Send(new GetOrdersQuery(1));
        Assert.Equal(cached.Value, again.Value);

        await host.Sender.Send(new CreateOrderCommand("a")); // başarılı: "orders" etiketi silinir
        Result<List<string>> fresh = await host.Sender.Send(new GetOrdersQuery(1));
        Assert.NotEqual(cached.Value, fresh.Value);
    }
}
