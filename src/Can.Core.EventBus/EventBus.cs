using System.Diagnostics;
using System.Text.Json;
using Can.Core.Domain.Events;
using Can.Core.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.EventBus;

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
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
    }
}
