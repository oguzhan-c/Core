using System.Text.Json;
using Can.Core.Domain.Events;
using Can.Core.Mediator;
using Can.Core.Mediator.DependencyInjection;
using Can.Core.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Can.Core.EventBus.Tests;

[IntegrationEventName("billing.payment-failed")]
public sealed record PaymentFailed(int OrderId) : DomainEvent, IIntegrationEvent;

public sealed class FailurePlan
{
    private int _calls;

    /// <summary>İlk kaç çağrı hata versin.</summary>
    public int FailTimes { get; set; }

    public bool Permanent { get; set; }

    public int Calls => _calls;

    public void Called() => Interlocked.Increment(ref _calls);
}

public sealed class PaymentFailedHandler(FailurePlan plan) : INotificationHandler<PaymentFailed>
{
    public Task Handle(PaymentFailed notification, CancellationToken cancellationToken)
    {
        plan.Called();
        if (plan.Permanent)
            throw new PermanentEventFailureException("geçersiz sipariş");
        if (plan.Calls <= plan.FailTimes)
            throw new TimeoutException("geçici hata");
        return Task.CompletedTask;
    }
}

public sealed class TestBrokerOptions : EventBrokerOptions;

public class EventConsumerTests
{
    private static ServiceProvider Build(bool inbox = true)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Log>();
        services.AddSingleton<FailurePlan>();
        services.AddCanMultiTenancy(o => o.Tenants = [new TenantInfo { Id = "t-a", Identifier = "a" }]);
        services.AddCanMediator(cfg => cfg.RegisterServicesFromAssemblyContaining<EventConsumerTests>());
        services.AddCanEventBus(typeof(EventConsumerTests).Assembly);
        if (inbox)
            services.AddCanInMemoryInbox();
        return services.BuildServiceProvider();
    }

    private static EventConsumer Consumer(IServiceProvider provider, Action<TestBrokerOptions>? configure = null)
    {
        var options = new TestBrokerOptions { ConsumerName = "billing", ImmediateRetryDelay = TimeSpan.Zero };
        configure?.Invoke(options);
        return new EventConsumer(provider.GetRequiredService<EventDispatcher>(), options, NullLogger.Instance);
    }

    private static EventEnvelope Envelope(IIntegrationEvent e, string name) =>
        new(e.EventId, name, JsonSerializer.Serialize(e, e.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web)), "t-a", e.OccurredAt);

    [Fact]
    public async Task Successful_event_completes()
    {
        await using ServiceProvider provider = Build();
        FailurePlan plan = provider.GetRequiredService<FailurePlan>();

        ConsumeOutcome outcome = await Consumer(provider).ConsumeAsync(Envelope(new PaymentFailed(1), "billing.payment-failed"), 1, CancellationToken.None);

        Assert.Equal(ConsumeAction.Complete, outcome.Action);
        Assert.Null(outcome.Reason);
        Assert.Equal(1, plan.Calls);
    }

    [Fact]
    public async Task Transient_failure_is_retried_immediately_before_giving_up()
    {
        await using ServiceProvider provider = Build();
        FailurePlan plan = provider.GetRequiredService<FailurePlan>();
        plan.FailTimes = 2;

        ConsumeOutcome outcome = await Consumer(provider).ConsumeAsync(Envelope(new PaymentFailed(1), "billing.payment-failed"), 1, CancellationToken.None);

        Assert.Equal(ConsumeAction.Complete, outcome.Action);
        Assert.Equal(3, plan.Calls); // 1 + 2 anında deneme
    }

    [Fact]
    public async Task Persistent_failure_schedules_delayed_retry_then_dead_letters()
    {
        await using ServiceProvider provider = Build();
        FailurePlan plan = provider.GetRequiredService<FailurePlan>();
        plan.FailTimes = int.MaxValue;
        EventConsumer consumer = Consumer(provider, o =>
        {
            o.ImmediateRetries = 1;
            o.RetryDelays.Clear();
            o.RetryDelays.Add(TimeSpan.FromSeconds(5));
            o.RetryDelays.Add(TimeSpan.FromMinutes(1));
        });
        EventEnvelope envelope = Envelope(new PaymentFailed(1), "billing.payment-failed");

        ConsumeOutcome first = await consumer.ConsumeAsync(envelope, 1, CancellationToken.None);
        Assert.Equal(ConsumeAction.Retry, first.Action);
        Assert.Equal(TimeSpan.FromSeconds(5), first.Delay);
        Assert.IsType<TimeoutException>(first.Error);
        Assert.Equal(2, plan.Calls);

        ConsumeOutcome second = await consumer.ConsumeAsync(envelope, 2, CancellationToken.None);
        Assert.Equal(ConsumeAction.Retry, second.Action);
        Assert.Equal(TimeSpan.FromMinutes(1), second.Delay);

        Assert.Equal(3, consumer.MaxAttempts);
        ConsumeOutcome last = await consumer.ConsumeAsync(envelope, 3, CancellationToken.None);
        Assert.Equal(ConsumeAction.DeadLetter, last.Action);
        Assert.Equal("retries-exhausted", last.Reason);
    }

    [Fact]
    public async Task Permanent_failure_goes_to_dead_letter_without_retries()
    {
        await using ServiceProvider provider = Build();
        FailurePlan plan = provider.GetRequiredService<FailurePlan>();
        plan.Permanent = true;

        ConsumeOutcome outcome = await Consumer(provider).ConsumeAsync(Envelope(new PaymentFailed(1), "billing.payment-failed"), 1, CancellationToken.None);

        Assert.Equal(ConsumeAction.DeadLetter, outcome.Action);
        Assert.Equal("permanent-failure", outcome.Reason);
        Assert.Equal(1, plan.Calls);
    }

    [Fact]
    public async Task Invalid_payload_is_permanent()
    {
        await using ServiceProvider provider = Build();
        var envelope = new EventEnvelope(Guid.NewGuid(), "billing.payment-failed", "{ bozuk", null, DateTimeOffset.UtcNow);

        ConsumeOutcome outcome = await Consumer(provider).ConsumeAsync(envelope, 1, CancellationToken.None);

        Assert.Equal(ConsumeAction.DeadLetter, outcome.Action);
        Assert.IsAssignableFrom<JsonException>(outcome.Error);
    }

    [Fact]
    public async Task Unknown_event_is_completed_and_skipped()
    {
        await using ServiceProvider provider = Build();
        var envelope = new EventEnvelope(Guid.NewGuid(), "other.service-event", "{}", null, DateTimeOffset.UtcNow);

        ConsumeOutcome outcome = await Consumer(provider).ConsumeAsync(envelope, 1, CancellationToken.None);

        Assert.Equal(ConsumeAction.Complete, outcome.Action);
        Assert.Equal("unknown-event", outcome.Reason);
    }

    [Fact]
    public async Task Inbox_skips_redelivered_event()
    {
        await using ServiceProvider provider = Build();
        FailurePlan plan = provider.GetRequiredService<FailurePlan>();
        EventConsumer consumer = Consumer(provider);
        EventEnvelope envelope = Envelope(new PaymentFailed(1), "billing.payment-failed");

        await consumer.ConsumeAsync(envelope, 1, CancellationToken.None);
        ConsumeOutcome again = await consumer.ConsumeAsync(envelope, 1, CancellationToken.None);

        Assert.Equal("duplicate", again.Reason);
        Assert.Equal(1, plan.Calls);

        // başka tüketici aynı event'i ayrıca işler
        await Consumer(provider, o => o.ConsumerName = "shipping").ConsumeAsync(envelope, 1, CancellationToken.None);
        Assert.Equal(2, plan.Calls);
    }

    [Fact]
    public async Task Without_inbox_redelivery_runs_handlers_again()
    {
        await using ServiceProvider provider = Build(inbox: false);
        FailurePlan plan = provider.GetRequiredService<FailurePlan>();
        EventConsumer consumer = Consumer(provider);
        EventEnvelope envelope = Envelope(new PaymentFailed(1), "billing.payment-failed");

        await consumer.ConsumeAsync(envelope, 1, CancellationToken.None);
        await consumer.ConsumeAsync(envelope, 1, CancellationToken.None);

        Assert.Equal(2, plan.Calls);
    }

    [Fact]
    public async Task Failed_handler_is_not_recorded_in_inbox()
    {
        await using ServiceProvider provider = Build();
        FailurePlan plan = provider.GetRequiredService<FailurePlan>();
        plan.FailTimes = 1;
        EventConsumer consumer = Consumer(provider, o => o.ImmediateRetries = 0);
        EventEnvelope envelope = Envelope(new PaymentFailed(1), "billing.payment-failed");

        Assert.Equal(ConsumeAction.Retry, (await consumer.ConsumeAsync(envelope, 1, CancellationToken.None)).Action);
        ConsumeOutcome retried = await consumer.ConsumeAsync(envelope, 2, CancellationToken.None);

        Assert.Equal(ConsumeAction.Complete, retried.Action);
        Assert.Null(retried.Reason); // gerçekten işlendi, "duplicate" değil
        Assert.Equal(2, plan.Calls);
    }

    [Fact]
    public async Task Subscriptions_are_events_with_handlers_unless_listed()
    {
        await using ServiceProvider provider = Build();

        IReadOnlyList<string> names = EventConsumer.Subscriptions(provider, new TestBrokerOptions());
        Assert.Contains("billing.payment-failed", names);
        Assert.Contains("sales.order-shipped", names);
        Assert.DoesNotContain(typeof(StockLow).FullName!, names); // handler'ı yok

        var listed = new TestBrokerOptions();
        listed.Events.Add("billing.payment-failed");
        Assert.Equal(["billing.payment-failed"], EventConsumer.Subscriptions(provider, listed));
    }

    [Fact]
    public void Headers_round_trip_and_keep_custom_headers()
    {
        var envelope = new EventEnvelope(Guid.NewGuid(), "sales.order-shipped", "{}", "t-a", new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(3)))
        {
            Headers = new Dictionary<string, string> { ["traceparent"] = "00-abc-def-01" },
        };

        Dictionary<string, string> headers = EventHeaders.From(envelope);
        Assert.Equal("1", headers[EventHeaders.Attempt]);

        EventEnvelope back = EventHeaders.ToEnvelope(headers, "{}");
        Assert.Equal(envelope.EventId, back.EventId);
        Assert.Equal(envelope.EventName, back.EventName);
        Assert.Equal("t-a", back.TenantId);
        Assert.Equal(envelope.OccurredAt, back.OccurredAt);
        Assert.Equal("00-abc-def-01", back.Headers["traceparent"]);
        Assert.False(back.Headers.ContainsKey(EventHeaders.EventId)); // sistem başlıkları zarfın başlıklarına karışmaz
    }

    [Fact]
    public void Retry_and_dead_letter_headers()
    {
        Dictionary<string, string> headers = EventHeaders.From(new EventEnvelope(Guid.NewGuid(), "x", "{}", null, DateTimeOffset.UtcNow));
        var at = new DateTimeOffset(2026, 5, 1, 10, 0, 0, TimeSpan.Zero);

        Dictionary<string, string> retry = EventHeaders.ForRetry(headers, "billing", 3, at);
        Assert.Equal(3, EventHeaders.AttemptOf(retry));
        Assert.Equal(at, EventHeaders.NotBeforeOf(retry));
        Assert.Equal("billing", retry[EventHeaders.Consumer]);

        Dictionary<string, string> dead = EventHeaders.ForDeadLetter(retry, "billing", "retries-exhausted", new InvalidOperationException(new string('a', 2000)), at);
        Assert.Equal(1000, dead[EventHeaders.Error].Length);
        Assert.Equal(typeof(InvalidOperationException).FullName, dead[EventHeaders.ErrorType]);
        Assert.False(dead.ContainsKey(EventHeaders.NotBefore));
    }

    [Fact]
    public void Missing_required_headers_are_rejected()
    {
        Assert.Throws<FormatException>(() => EventHeaders.ToEnvelope(new Dictionary<string, string> { [EventHeaders.EventName] = "x" }, "{}"));
        Assert.Throws<FormatException>(() => EventHeaders.ToEnvelope(new Dictionary<string, string> { [EventHeaders.EventId] = Guid.NewGuid().ToString() }, "{}"));
    }
}
