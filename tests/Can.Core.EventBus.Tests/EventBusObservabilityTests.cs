using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Can.Core.Domain.Events;
using Can.Core.Mediator.DependencyInjection;
using Can.Core.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Can.Core.EventBus.Tests;

public class EventBusObservabilityTests
{
    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Log>();
        services.AddSingleton<FailurePlan>();
        services.AddCanMultiTenancy(o => o.Tenants = [new TenantInfo { Id = "t-a", Identifier = "a" }]);
        services.AddCanMediator(cfg => cfg.RegisterServicesFromAssemblyContaining<EventBusObservabilityTests>());
        services.AddCanEventBus(typeof(EventBusObservabilityTests).Assembly);
        services.AddCanInMemoryInbox();
        return services.BuildServiceProvider();
    }

    private static EventEnvelope Envelope(IIntegrationEvent e) =>
        new(e.EventId, "billing.payment-failed", JsonSerializer.Serialize(e, e.GetType(), new JsonSerializerOptions(JsonSerializerDefaults.Web)), "t-a", e.OccurredAt);

    [Fact]
    public async Task Consumer_records_outcome_and_lag_metrics()
    {
        string consumerName = "metrics-" + Guid.NewGuid().ToString("N")[..8];
        var outcomes = new ConcurrentQueue<string>();
        var lags = new ConcurrentQueue<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == EventBusTelemetry.Name)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            if (instrument.Name == "can.eventbus.consumed" && Tag(tags, "can.consumer") == consumerName)
                outcomes.Enqueue(Tag(tags, "can.outcome")!);
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "can.eventbus.lag" && Tag(tags, "can.consumer") == consumerName)
                lags.Enqueue(value);
        });
        listener.Start();

        await using ServiceProvider provider = Build();
        FailurePlan plan = provider.GetRequiredService<FailurePlan>();
        var consumer = new EventConsumer(provider.GetRequiredService<EventDispatcher>(), new TestBrokerOptions { ConsumerName = consumerName, ImmediateRetries = 0 }, NullLogger.Instance);
        EventEnvelope ok = Envelope(new PaymentFailed(1));

        await consumer.ConsumeAsync(ok, 1, CancellationToken.None);
        await consumer.ConsumeAsync(ok, 1, CancellationToken.None); // inbox: tekrar
        plan.FailTimes = int.MaxValue;
        await consumer.ConsumeAsync(Envelope(new PaymentFailed(2)), 1, CancellationToken.None); // retry
        plan.Permanent = true;
        await consumer.ConsumeAsync(Envelope(new PaymentFailed(3)), 2, CancellationToken.None); // dead_letter (lag yok: ilk deneme değil)

        Assert.Equal(new[] { "completed", "duplicate", "retry", "dead_letter" }, outcomes.ToArray());
        Assert.Equal(3, lags.Count);
        Assert.All(lags, lag => Assert.True(lag >= 0));
    }

    private static string? Tag(ReadOnlySpan<KeyValuePair<string, object?>> tags, string key)
    {
        foreach (KeyValuePair<string, object?> tag in tags)
        {
            if (tag.Key == key)
                return tag.Value as string;
        }

        return null;
    }

    [Fact]
    public void Replay_headers_reset_attempts_and_clear_the_error()
    {
        var dead = new Dictionary<string, string>
        {
            [EventHeaders.EventId] = Guid.NewGuid().ToString("D"),
            [EventHeaders.EventName] = "billing.payment-failed",
            [EventHeaders.Attempt] = "5",
            [EventHeaders.NotBefore] = DateTimeOffset.UtcNow.ToString("O"),
            [EventHeaders.Error] = "zaman aşımı",
            [EventHeaders.ErrorType] = typeof(TimeoutException).FullName!,
            [EventHeaders.FailedAt] = DateTimeOffset.UtcNow.ToString("O"),
            [EventHeaders.ReplayCount] = "2",
            ["traceparent"] = "00-abc",
        };

        Dictionary<string, string> replay = EventHeaders.ForReplay(dead, "billing");

        Assert.Equal("1", replay[EventHeaders.Attempt]);
        Assert.Equal("3", replay[EventHeaders.ReplayCount]);
        Assert.Equal("billing", replay[EventHeaders.Consumer]);
        Assert.Equal("00-abc", replay["traceparent"]);
        Assert.False(replay.ContainsKey(EventHeaders.Error));
        Assert.False(replay.ContainsKey(EventHeaders.ErrorType));
        Assert.False(replay.ContainsKey(EventHeaders.FailedAt));
        Assert.False(replay.ContainsKey(EventHeaders.NotBefore));

        EventEnvelope envelope = EventHeaders.ToEnvelope(replay, "{}");
        Assert.False(envelope.Headers.ContainsKey(EventHeaders.ReplayCount)); // sistem başlığı: handler'a sızmaz
    }

    [Fact]
    public void Dead_letter_message_exposes_headers()
    {
        Guid id = Guid.NewGuid();
        var message = new DeadLetterMessage(new Dictionary<string, string>
        {
            [EventHeaders.EventId] = id.ToString("D"),
            [EventHeaders.EventName] = "billing.payment-failed",
            [EventHeaders.TenantId] = "t-a",
            [EventHeaders.ErrorType] = "System.TimeoutException",
            [EventHeaders.Error] = "zaman aşımı",
            [EventHeaders.FailedAt] = "2026-10-10T12:00:00.0000000+00:00",
            [EventHeaders.ReplayCount] = "1",
        });

        Assert.Equal(id, message.EventId);
        Assert.Equal("billing.payment-failed", message.EventName);
        Assert.Equal("t-a", message.TenantId);
        Assert.Equal("System.TimeoutException", message.ErrorType);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero), message.FailedAt);
        Assert.Equal(1, message.ReplayCount);
        Assert.Null(new DeadLetterMessage(new Dictionary<string, string>()).EventId);
    }

    private sealed class FakeBroker(EventBrokerHealthReport report) : IEventBrokerHealth
    {
        public Task<EventBrokerHealthReport> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(report);
    }

    private static async Task<HealthCheckResult> CheckAsync(IEventBrokerHealth? broker, long threshold = 0)
    {
        var check = new EventBusHealthCheck(broker, new EventBusHealthCheckOptions { DeadLetterThreshold = threshold });
        var context = new HealthCheckContext { Registration = new HealthCheckRegistration("eventbus", check, HealthStatus.Unhealthy, null) };
        return await check.CheckHealthAsync(context, CancellationToken.None);
    }

    [Fact]
    public async Task Health_check_maps_broker_report()
    {
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(null)).Status); // bellek içi

        HealthCheckResult healthy = await CheckAsync(new FakeBroker(new("rabbitmq", true, true, 0)));
        Assert.Equal(HealthStatus.Healthy, healthy.Status);
        Assert.Equal(0L, healthy.Data["deadLetters"]);

        HealthCheckResult down = await CheckAsync(new FakeBroker(new("rabbitmq", false, false, null, "bağlantı reddedildi")));
        Assert.Equal(HealthStatus.Unhealthy, down.Status);
        Assert.Contains("bağlantı reddedildi", down.Description, StringComparison.Ordinal);

        Assert.Equal(HealthStatus.Unhealthy, (await CheckAsync(new FakeBroker(new("kafka", true, false, 0)))).Status);
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(new FakeBroker(new("kafka", true, null, null)))).Status); // yalnızca yayınlayan

        Assert.Equal(HealthStatus.Degraded, (await CheckAsync(new FakeBroker(new("kafka", true, true, 3)))).Status);
        Assert.Equal(HealthStatus.Healthy, (await CheckAsync(new FakeBroker(new("kafka", true, true, 3)), threshold: 5)).Status);
    }

    [Fact]
    public async Task In_memory_transport_has_no_dead_letter_queue()
    {
        await using ServiceProvider provider = Build();
        Assert.Null(provider.GetService<IDeadLetterQueue>());
        Assert.Null(provider.GetService<IEventBrokerHealth>());
    }
}
