using System.Collections.Concurrent;
using Can.Core.Domain.Events;
using Can.Core.Mediator;
using Can.Core.Mediator.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Can.Core.EventBus.Brokers.Tests;

[IntegrationEventName("brokertest.order-placed")]
public sealed record BrokerOrderPlaced(int Id) : DomainEvent, IIntegrationEvent;

[IntegrationEventName("brokertest.payment-failed")]
public sealed record BrokerPaymentFailed(int Id) : DomainEvent, IIntegrationEvent;

public sealed class Received
{
    private int _failures;

    public ConcurrentQueue<int> Orders { get; } = new();

    public int Failures => _failures;

    public void Failed() => Interlocked.Increment(ref _failures);
}

public sealed class BrokerOrderPlacedHandler(Received received) : INotificationHandler<BrokerOrderPlaced>
{
    public Task Handle(BrokerOrderPlaced notification, CancellationToken cancellationToken)
    {
        received.Orders.Enqueue(notification.Id);
        return Task.CompletedTask;
    }
}

public sealed class BrokerPaymentFailedHandler(Received received) : INotificationHandler<BrokerPaymentFailed>
{
    public Task Handle(BrokerPaymentFailed notification, CancellationToken cancellationToken)
    {
        received.Failed();
        throw new TimeoutException("ödeme servisi yanıt vermedi");
    }
}

/// <summary>Gerçek broker'a karşı tek servis: bus + taşıyıcı + bellek içi inbox; tüketici hazır olunca döner.</summary>
internal sealed class BrokerHost : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly IHostedService[] _hosted;

    private BrokerHost(ServiceProvider provider, IHostedService[] hosted)
    {
        _provider = provider;
        _hosted = hosted;
    }

    public IServiceProvider Services => _provider;

    public Received Received => _provider.GetRequiredService<Received>();

    /// <summary>Test başına benzersiz ad (kuyruk/topic/subscription çakışmasın).</summary>
    public static string UniqueName(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    public static async Task<BrokerHost> StartAsync(Action<IServiceCollection> transport, Func<IHostedService, Task> started)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Received>();
        services.AddCanMediator(cfg => cfg.RegisterServicesFromAssemblyContaining<BrokerHost>());
        services.AddCanEventBus(typeof(BrokerHost).Assembly);
        services.AddCanInMemoryInbox();
        transport(services);

        ServiceProvider provider = services.BuildServiceProvider();
        IHostedService[] hosted = provider.GetServices<IHostedService>().ToArray();
        foreach (IHostedService service in hosted)
            await service.StartAsync(CancellationToken.None);
        foreach (IHostedService service in hosted)
            await started(service).WaitAsync(TimeSpan.FromSeconds(60));
        return new BrokerHost(provider, hosted);
    }

    public async Task PublishAsync(IIntegrationEvent integrationEvent)
    {
        await using AsyncServiceScope scope = _provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(integrationEvent);
    }

    public static async Task WaitUntilAsync(Func<bool> condition, int seconds = 30)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
                throw new TimeoutException("Beklenen durum oluşmadı.");
            await Task.Delay(200);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (IHostedService service in _hosted)
            await service.StopAsync(CancellationToken.None);
        await _provider.DisposeAsync();
    }
}

/// <summary>Her broker için aynı üç senaryo.</summary>
internal static class BrokerScenarios
{
    public static async Task DeliversEventAsync(BrokerHost host)
    {
        await host.PublishAsync(new BrokerOrderPlaced(7));
        await BrokerHost.WaitUntilAsync(() => host.Received.Orders.Contains(7));
    }

    public static async Task IgnoresDuplicateDeliveryAsync(BrokerHost host)
    {
        var placed = new BrokerOrderPlaced(8);
        await host.PublishAsync(placed);
        await host.PublishAsync(placed); // aynı EventId: broker iki kez teslim etmiş gibi
        await host.PublishAsync(new BrokerOrderPlaced(9));
        await BrokerHost.WaitUntilAsync(() => host.Received.Orders.Contains(8) && host.Received.Orders.Contains(9));
        await Task.Delay(1000);
        Assert.Equal(1, host.Received.Orders.Count(id => id == 8));
    }

    /// <summary>RetryDelays = [1 sn], ImmediateRetries = 0 iken: ilk deneme + 1 gecikmeli deneme, sonra DLQ.</summary>
    public static async Task RetriesThenDeadLettersAsync(BrokerHost host, Func<Task<IReadOnlyDictionary<string, string>?>> readDeadLetter)
    {
        await host.PublishAsync(new BrokerPaymentFailed(1));
        await BrokerHost.WaitUntilAsync(() => host.Received.Failures >= 2);

        IReadOnlyDictionary<string, string>? headers = null;
        await BrokerHost.WaitUntilAsync(() =>
        {
            headers ??= readDeadLetter().GetAwaiter().GetResult();
            return headers is not null;
        });

        Assert.Equal(2, host.Received.Failures);
        Assert.Equal(typeof(TimeoutException).FullName, headers![EventHeaders.ErrorType]);
        Assert.Equal("brokertest.payment-failed", headers[EventHeaders.EventName]);
        Assert.Equal("2", headers[EventHeaders.Attempt]);
    }
}
