using System.Text.Json;
using Can.Core.Domain.Events;
using Can.Core.EventBus;
using Can.Core.Mediator;
using Can.Core.Persistence.DependencyInjection;
using Can.Core.Persistence.Inbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Persistence.Tests;

/// <summary>Broker'dan gelen ve veritabanına yazan event.</summary>
public sealed record CategoryRequested(string Name) : DomainEvent, IIntegrationEvent;

public sealed class CategoryRequestedHandler(TestDbContext db, FailureSwitch failure) : INotificationHandler<CategoryRequested>
{
    public async Task Handle(CategoryRequested notification, CancellationToken cancellationToken)
    {
        db.Categories.Add(new Category(notification.Name));
        await db.SaveChangesAsync(cancellationToken);
        if (failure.Fail)
            throw new InvalidOperationException("kayıttan sonra hata");
    }
}

public class InboxTests
{
    private static EventEnvelope Envelope(CategoryRequested e) =>
        new(e.EventId, typeof(CategoryRequested).FullName!, JsonSerializer.Serialize(e, new JsonSerializerOptions(JsonSerializerDefaults.Web)), null, e.OccurredAt);

    private static async Task<(int Categories, int Inbox)> CountAsync(TestHost host)
    {
        await using AsyncServiceScope scope = host.CreateScope();
        TestDbContext db = scope.ServiceProvider.GetRequiredService<TestDbContext>();
        return (await db.Categories.CountAsync(), await db.Set<InboxMessage>().CountAsync());
    }

    [Fact]
    public async Task Event_is_processed_once_per_consumer_and_recorded_with_handler_changes()
    {
        await using TestHost host = await TestHost.CreateAsync(services =>
        {
            services.AddCanEventBus(typeof(TestHost).Assembly);
            services.AddCanInbox<TestDbContext>();
        });
        EventDispatcher dispatcher = host.Services.GetRequiredService<EventDispatcher>();
        EventEnvelope envelope = Envelope(new CategoryRequested("Kırtasiye"));

        Assert.Equal(DispatchResult.Handled, await dispatcher.DispatchAsync(envelope, "billing"));
        Assert.Equal(DispatchResult.Duplicate, await dispatcher.DispatchAsync(envelope, "billing"));
        Assert.Equal((1, 1), await CountAsync(host));

        // başka tüketici ayrıca işler
        Assert.Equal(DispatchResult.Handled, await dispatcher.DispatchAsync(envelope, "shipping"));
        Assert.Equal((2, 2), await CountAsync(host));

        await using AsyncServiceScope scope = host.CreateScope();
        InboxMessage record = await scope.ServiceProvider.GetRequiredService<TestDbContext>().Set<InboxMessage>().FirstAsync(m => m.Consumer == "billing");
        Assert.Equal(envelope.EventId, record.EventId);
        Assert.Equal(typeof(CategoryRequested).FullName, record.EventName);
        Assert.Equal(host.Clock.GetUtcNow().UtcDateTime, record.ProcessedAt);
    }

    [Fact]
    public async Task Failed_handler_rolls_back_its_changes_and_inbox_record()
    {
        await using TestHost host = await TestHost.CreateAsync(services =>
        {
            services.AddCanEventBus(typeof(TestHost).Assembly);
            services.AddCanInbox<TestDbContext>();
        });
        EventDispatcher dispatcher = host.Services.GetRequiredService<EventDispatcher>();
        FailureSwitch failure = host.Services.GetRequiredService<FailureSwitch>();
        EventEnvelope envelope = Envelope(new CategoryRequested("Kırtasiye"));

        failure.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(envelope, "billing"));
        Assert.Equal((0, 0), await CountAsync(host)); // handler SaveChanges çağırmıştı ama transaction geri alındı

        failure.Fail = false;
        Assert.Equal(DispatchResult.Handled, await dispatcher.DispatchAsync(envelope, "billing"));
        Assert.Equal((1, 1), await CountAsync(host));
    }

    [Fact]
    public async Task Cleanup_deletes_expired_records()
    {
        await using TestHost host = await TestHost.CreateAsync(services =>
        {
            services.AddCanEventBus(typeof(TestHost).Assembly);
            services.AddCanInbox<TestDbContext>(o => o.Retention = TimeSpan.FromDays(1));
        });
        EventDispatcher dispatcher = host.Services.GetRequiredService<EventDispatcher>();
        await dispatcher.DispatchAsync(Envelope(new CategoryRequested("A")), "billing");

        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var job = ActivatorUtilities.CreateInstance<InboxCleanupJob<TestDbContext>>(scope.ServiceProvider);
            await job.ExecuteAsync(CancellationToken.None);
        }

        Assert.Equal(1, (await CountAsync(host)).Inbox); // henüz süresi dolmadı

        host.Clock.Now += TimeSpan.FromDays(2);
        await using (AsyncServiceScope scope = host.CreateScope())
        {
            var job = ActivatorUtilities.CreateInstance<InboxCleanupJob<TestDbContext>>(scope.ServiceProvider);
            await job.ExecuteAsync(CancellationToken.None);
        }

        Assert.Equal(0, (await CountAsync(host)).Inbox);
    }
}
