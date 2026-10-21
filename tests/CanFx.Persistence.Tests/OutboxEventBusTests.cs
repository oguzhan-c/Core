using CanFx.EventBus;
using CanFx.Persistence.Outbox;
using CanFx.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.Persistence.Tests;

public sealed class RecordingTransport : IEventTransport
{
    public List<EventEnvelope> Sent { get; } = [];

    public Task SendAsync(EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        Sent.Add(envelope);
        return Task.CompletedTask;
    }
}

public class OutboxEventBusTests
{
    [Fact]
    public async Task Outbox_publishes_through_event_bus_when_registered()
    {
        var transport = new RecordingTransport();
        await using TestHost host = await TestHost.CreateAsync(services =>
        {
            services.AddCanEventBus(typeof(TestHost).Assembly);
            services.AddSingleton<IEventTransport>(transport);
        });

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IRepository<Product, int>>();
            Product product = await repository.AddAsync(Product.Create("Defter", 5));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
            product.Discontinue();
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        var processor = ActivatorUtilities.CreateInstance<OutboxProcessor<TestDbContext>>(host.Services, new OutboxOptions());
        Assert.Equal(1, await processor.ProcessAsync());

        EventEnvelope envelope = Assert.Single(transport.Sent);
        Assert.Equal(typeof(ProductDiscontinued).FullName, envelope.EventName);
        Assert.Contains("\"name\":\"Defter\"", envelope.Payload, StringComparison.Ordinal);

        // Taşıyıcı yalnızca kaydetti; yerel handler'lar bus yokken olduğu gibi doğrudan çağrılmadı.
        Assert.DoesNotContain(host.Events.Entries, e => e.StartsWith("discontinued", StringComparison.Ordinal));
    }
}
