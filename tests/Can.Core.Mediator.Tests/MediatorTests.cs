using Can.Core.Domain.Events;
using Can.Core.Mediator.DependencyInjection;
using Can.Core.Mediator.Publishers;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Mediator.Tests;

public class MediatorTests
{
    private static ServiceProvider Build(Action<MediatorConfiguration>? extra = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Log>();
        services.AddCanMediator(cfg =>
        {
            cfg.RegisterServicesFromAssemblyContaining<MediatorTests>();
            extra?.Invoke(cfg);
        });

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public async Task Send_returns_handler_response()
    {
        using ServiceProvider provider = Build();
        var mediator = provider.GetRequiredService<IMediator>();

        string response = await mediator.Send(new Ping("hi"));

        Assert.Equal("Pong: hi", response);
    }

    [Fact]
    public async Task ISender_and_IPublisher_resolve_to_mediator()
    {
        using ServiceProvider provider = Build();

        var sender = provider.GetRequiredService<ISender>();
        Assert.NotNull(provider.GetRequiredService<IPublisher>());

        Assert.Equal("Pong: x", await sender.Send(new Ping("x")));
    }

    [Fact]
    public async Task Void_request_handler_runs()
    {
        using ServiceProvider provider = Build();
        var mediator = provider.GetRequiredService<IMediator>();
        var log = provider.GetRequiredService<Log>();

        await mediator.Send(new DoWork(7));

        Assert.Contains("work:7", log.Entries);
    }

    [Fact]
    public async Task Send_object_returns_boxed_response()
    {
        using ServiceProvider provider = Build();
        var mediator = provider.GetRequiredService<IMediator>();

        object? pong = await mediator.Send((object)new Ping("obj"));
        object? unit = await mediator.Send((object)new DoWork(1));

        Assert.Equal("Pong: obj", pong);
        Assert.Equal(Unit.Value, unit);
    }

    [Fact]
    public async Task Send_object_rejects_non_request()
    {
        using ServiceProvider provider = Build();
        var mediator = provider.GetRequiredService<IMediator>();

        await Assert.ThrowsAsync<ArgumentException>(() => mediator.Send((object)"not a request"));
    }

    [Fact]
    public async Task Missing_handler_throws_HandlerNotFoundException()
    {
        using ServiceProvider provider = Build();
        var mediator = provider.GetRequiredService<IMediator>();

        var ex = await Assert.ThrowsAsync<HandlerNotFoundException>(() => mediator.Send(new Orphan()));
        Assert.Equal(typeof(Orphan), ex.RequestType);
    }

    [Fact]
    public async Task Behaviors_run_in_registration_order()
    {
        using ServiceProvider provider = Build(cfg =>
        {
            cfg.AddOpenBehavior(typeof(OuterBehavior<,>));
            cfg.AddOpenBehavior(typeof(InnerBehavior<,>));
        });
        var mediator = provider.GetRequiredService<IMediator>();
        var log = provider.GetRequiredService<Log>();

        await mediator.Send(new Ping("order"));

        Assert.Equal(
            new[] { "outer:before", "inner:before", "handler", "inner:after", "outer:after" },
            log.Entries
        );
    }

    [Fact]
    public async Task Constrained_behavior_only_applies_to_marked_requests()
    {
        using ServiceProvider provider = Build(cfg => cfg.AddOpenBehavior(typeof(AuditBehavior<,>)));
        var mediator = provider.GetRequiredService<IMediator>();
        var log = provider.GetRequiredService<Log>();

        await mediator.Send(new Ping("plain"));
        await mediator.Send(new AuditedPing("audited"));

        Assert.Equal(new[] { "handler", "audit:AuditedPing" }, log.Entries);
    }

    [Fact]
    public async Task Behavior_can_short_circuit_the_handler()
    {
        using ServiceProvider provider = Build(cfg => cfg.AddOpenBehavior(typeof(FakeCacheBehavior<,>)));
        var mediator = provider.GetRequiredService<IMediator>();
        var log = provider.GetRequiredService<Log>();

        string response = await mediator.Send(new CachedPing());

        Assert.Equal("from-cache", response);
        Assert.DoesNotContain("cached-handler", log.Entries);
    }

    [Fact]
    public async Task Publish_runs_exact_and_base_type_handlers()
    {
        using ServiceProvider provider = Build();
        var publisher = provider.GetRequiredService<IPublisher>();
        var log = provider.GetRequiredService<Log>();

        // Interceptor'ın yapacağı gibi: statik tip IDomainEvent, gerçek tip OrderConfirmed
        IDomainEvent domainEvent = new OrderConfirmed(Guid.NewGuid());
        await publisher.Publish(domainEvent);

        Assert.Equal(3, log.Entries.Count);
        Assert.Contains("email", log.Entries);
        Assert.Contains("stock", log.Entries);
        Assert.Contains("any:OrderConfirmed", log.Entries);
    }

    [Fact]
    public async Task Publish_without_handlers_does_nothing()
    {
        using ServiceProvider provider = Build();
        var publisher = provider.GetRequiredService<IPublisher>();

        await publisher.Publish(new NobodyListens());
        await publisher.Publish(new UserRegistered("a@b.c"));
    }

    [Fact]
    public async Task TaskWhenAll_publisher_can_be_configured()
    {
        using ServiceProvider provider = Build(cfg => cfg.NotificationPublisherType = typeof(TaskWhenAllPublisher));
        var publisher = provider.GetRequiredService<IPublisher>();
        var log = provider.GetRequiredService<Log>();

        Assert.IsType<TaskWhenAllPublisher>(provider.GetRequiredService<INotificationPublisher>());

        await publisher.Publish(new OrderConfirmed(Guid.NewGuid()));

        Assert.Equal(3, log.Entries.Count);
    }

    [Fact]
    public async Task Stream_with_behavior()
    {
        using ServiceProvider provider = Build(cfg => cfg.AddOpenStreamBehavior(typeof(TimesTenStreamBehavior<,>)));
        var mediator = provider.GetRequiredService<IMediator>();

        var items = new List<int>();
        await foreach (int item in mediator.CreateStream(new CountTo(3)))
            items.Add(item);

        Assert.Equal(new[] { 10, 20, 30 }, items);
    }

    [Fact]
    public void Scanning_the_same_assembly_twice_does_not_duplicate_handlers()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Log>();
        services.AddCanMediator(cfg => cfg.RegisterServicesFromAssemblyContaining<MediatorTests>());
        services.AddCanMediator(cfg => cfg.RegisterServicesFromAssemblyContaining<MediatorTests>());

        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(2, provider.GetServices<INotificationHandler<OrderConfirmed>>().Count());
    }

    [Fact]
    public void AddOpenBehavior_rejects_non_behavior_types()
    {
        var configuration = new MediatorConfiguration();

        Assert.Throws<ArgumentException>(() => configuration.AddOpenBehavior(typeof(List<>)));
        Assert.Throws<ArgumentException>(() => configuration.AddOpenBehavior(typeof(OuterBehavior<string, string>)));
    }

    [Fact]
    public void Handled_types_include_base_types_and_interfaces()
    {
        Type[] types = Mediator.GetHandledTypes(typeof(OrderConfirmed)).ToArray();

        Assert.Equal(typeof(OrderConfirmed), types[0]);
        Assert.Contains(typeof(DomainEvent), types);
        Assert.Contains(typeof(IDomainEvent), types);
        Assert.DoesNotContain(typeof(object), types);
    }
}
