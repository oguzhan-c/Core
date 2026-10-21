using System.Diagnostics;
using System.Text.Json;
using CanFx.Domain.Events;
using CanFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.EventBus;

internal static class EventJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Scoped: event'i, yayınlayan scope'un tenant'ıyla zarflayıp taşıyıcıya verir.</summary>
internal sealed class DefaultEventBus : IEventBus
{
    private readonly IEventTransport _transport;
    private readonly EventTypeRegistry _registry;
    private readonly TenantContext? _tenantContext;

    public DefaultEventBus(IEventTransport transport, EventTypeRegistry registry, IServiceProvider services)
    {
        _transport = transport;
        _registry = registry;
        _tenantContext = services.GetService<TenantContext>();
    }

    public async Task PublishAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        Type type = integrationEvent.GetType();
        string eventName = _registry.GetName(type);

        using Activity? activity = EventBusTelemetry.StartPublish(eventName);
        var envelope = new EventEnvelope(
            integrationEvent.EventId,
            eventName,
            JsonSerializer.Serialize(integrationEvent, type, EventJson.Options),
            _tenantContext?.TenantId,
            integrationEvent.OccurredAt
        )
        {
            Headers = EventBusTelemetry.CreateHeaders(),
        };

        try
        {
            await _transport.SendAsync(envelope, cancellationToken).ConfigureAwait(false);
            EventBusTelemetry.Published.Add(1, new TagList { { "can.event", eventName }, { "can.outcome", "published" } });
        }
        catch (Exception exception)
        {
            EventBusTelemetry.Published.Add(1, new TagList { { "can.event", eventName }, { "can.outcome", "failed" } });
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
    }
}
