using System.Collections.Concurrent;
using Can.Core.Domain.Events;
using Can.Core.Mediator;
using Can.Core.Mediator.DependencyInjection;
using Can.Core.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.EventBus.Tests;

[IntegrationEventName("sales.order-shipped")]
public sealed record OrderShipped(int OrderId, string Email) : DomainEvent, IIntegrationEvent;

public sealed record StockLow(int ProductId) : DomainEvent, IIntegrationEvent;

public sealed class Log
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyCollection<string> Entries => _entries.ToArray();

    public void Add(string entry) => _entries.Enqueue(entry);
}

public sealed class OrderShippedHandler(Log log, TenantContext tenant) : INotificationHandler<OrderShipped>
{
    public Task Handle(OrderShipped notification, CancellationToken cancellationToken)
    {
        log.Add($"shipped:{notification.OrderId}:{notification.Email}@{tenant.TenantId ?? "host"}:{tenant.Tenant?.Identifier ?? "-"}");
        return Task.CompletedTask;
    }
}

public sealed class RecordingTransport : IEventTransport
{
    public List<EventEnvelope> Sent { get; } = [];

    public Task SendAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        Sent.Add(envelope);
        return Task.CompletedTask;
    }
}

public class EventBusTests
{
    private static ServiceProvider Build(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Log>();
        services.AddCanMultiTenancy(o => o.Tenants = [new TenantInfo { Id = "t-a", Identifier = "a" }]);
        services.AddCanMediator(cfg => cfg.RegisterServicesFromAssemblyContaining<EventBusTests>());
        services.AddCanEventBus(typeof(EventBusTests).Assembly);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task In_memory_bus_delivers_to_handlers_with_publisher_tenant()
    {
        await using ServiceProvider provider = Build();

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set("t-a");
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(new OrderShipped(7, "ada@test.local"));
        }

        Assert.Equal(new[] { "shipped:7:ada@test.local@t-a:a" }, provider.GetRequiredService<Log>().Entries);
    }

    [Fact]
    public async Task Envelope_uses_stable_name_payload_and_tenant()
    {
        var transport = new RecordingTransport();
        await using ServiceProvider provider = Build(s => s.AddSingleton<IEventTransport>(transport));

        var shipped = new OrderShipped(7, "ada@test.local");
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set("t-a");
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(shipped);
            await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(new StockLow(3));
        }

        Assert.Equal(2, transport.Sent.Count);
        EventEnvelope envelope = transport.Sent[0];
        Assert.Equal("sales.order-shipped", envelope.EventName);
        Assert.Equal(shipped.EventId, envelope.EventId);
        Assert.Equal("t-a", envelope.TenantId);
        Assert.Contains("\"orderId\":7", envelope.Payload, StringComparison.Ordinal);
        Assert.Equal(typeof(StockLow).FullName, transport.Sent[1].EventName);
        Assert.Empty(provider.GetRequiredService<Log>().Entries);

        // Karşı tarafta (consumer) aynı zarf dağıtılır.
        Assert.True(await provider.GetRequiredService<EventDispatcher>().DispatchAsync(envelope));
        Assert.Equal(new[] { "shipped:7:ada@test.local@t-a:a" }, provider.GetRequiredService<Log>().Entries);
    }

    [Fact]
    public async Task Unknown_events_are_skipped()
    {
        await using ServiceProvider provider = Build();

        var envelope = new EventEnvelope(Guid.NewGuid(), "baska-servis.event", "{}", null, DateTimeOffset.UtcNow);

        Assert.False(await provider.GetRequiredService<EventDispatcher>().DispatchAsync(envelope));
    }

    [Fact]
    public void Duplicate_event_names_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => new EventTypeRegistry([typeof(DuplicateA), typeof(DuplicateB)]));
    }

    // private: taramaya girmez (yalnızca public tipler taranır)
    [IntegrationEventName("dup")]
    private sealed record DuplicateA : DomainEvent, IIntegrationEvent;

    [IntegrationEventName("dup")]
    private sealed record DuplicateB : DomainEvent, IIntegrationEvent;
}
