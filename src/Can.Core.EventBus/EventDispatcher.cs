using System.Diagnostics;
using System.Text.Json;
using Can.Core.Domain.Events;
using Can.Core.Mediator;
using Can.Core.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Can.Core.EventBus;

/// <summary>
/// Gelen zarfı event'e çevirip kendi DI scope'unda, event'in tenant'ı adına <c>IPublisher</c> ile handler'lara dağıtır.
/// Taşıyıcılar (bellek içi, RabbitMQ consumer ...) mesaj aldığında bunu çağırır. Handler hatası çağırana fırlatılır;
/// tekrar deneme taşıyıcının (outbox, broker) işidir.
/// </summary>
public sealed partial class EventDispatcher
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EventTypeRegistry _registry;
    private readonly ILogger<EventDispatcher> _logger;

    public EventDispatcher(IServiceScopeFactory scopeFactory, EventTypeRegistry registry, ILogger<EventDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
        _logger = logger;
    }

    /// <returns>Event tanınıp handler'lara verildiyse <see langword="true"/>; bu serviste karşılığı yoksa <see langword="false"/>.</returns>
    public async Task<bool> DispatchAsync(EventEnvelope envelope, CancellationToken cancellationToken = default) =>
        await DispatchAsync(envelope, consumer: null, cancellationToken).ConfigureAwait(false) != DispatchResult.Unknown;

    /// <summary>
    /// Zarfı dağıtır. <paramref name="consumer"/> verilir ve <see cref="IInboxStore"/> kayıtlıysa event bu tüketicide
    /// yalnızca bir kez işlenir (tekrar gelen teslimler <see cref="DispatchResult.Duplicate"/>).
    /// </summary>
    public async Task<DispatchResult> DispatchAsync(EventEnvelope envelope, string? consumer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!_registry.TryGetType(envelope.EventName, out Type? type) || type is null)
        {
            // Başka servislerin event'leri de aynı kanaldan gelebilir; tanımadığımızı atlarız.
            LogUnknownEvent(envelope.EventName, envelope.EventId);
            return DispatchResult.Unknown;
        }

        using Activity? activity = EventBusTelemetry.StartProcess(envelope);
        try
        {
            var integrationEvent = (IIntegrationEvent?)JsonSerializer.Deserialize(envelope.Payload, type, EventJson.Options)
                ?? throw new JsonException($"'{envelope.EventName}' event'i okunamadı (boş içerik).");

            await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
            IServiceProvider services = scope.ServiceProvider;

            await RestoreTenantAsync(services, envelope.TenantId, cancellationToken).ConfigureAwait(false);
            IPublisher publisher = services.GetRequiredService<IPublisher>();

            if (consumer is not null && services.GetService<IInboxStore>() is { } inbox)
            {
                bool executed = await inbox
                    .ExecuteOnceAsync(services, consumer, envelope, ct => publisher.Publish(integrationEvent, ct), cancellationToken)
                    .ConfigureAwait(false);
                if (!executed)
                {
                    activity?.SetTag("can.duplicate", true);
                    LogDuplicate(envelope.EventName, envelope.EventId, consumer);
                    return DispatchResult.Duplicate;
                }

                return DispatchResult.Handled;
            }

            await publisher.Publish(integrationEvent, cancellationToken).ConfigureAwait(false);
            return DispatchResult.Handled;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
    }

    private static async Task RestoreTenantAsync(IServiceProvider services, string? tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(tenantId) || services.GetService<TenantContext>() is not { } context)
            return;

        TenantInfo? tenant = services.GetService<ITenantStore>() is { } store
            ? await store.FindAsync(tenantId, cancellationToken).ConfigureAwait(false)
            : null;

        if (tenant is not null)
            context.Set(tenant);
        else
            context.Set(tenantId);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Bilinmeyen event {EventName} ({EventId}) atlandı.")]
    private partial void LogUnknownEvent(string eventName, Guid eventId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{EventName} ({EventId}) {Consumer} tüketicisinde daha önce işlenmiş; atlandı.")]
    private partial void LogDuplicate(string eventName, Guid eventId, string consumer);
}

public enum DispatchResult
{
    /// <summary>Handler'lar çalıştı.</summary>
    Handled,

    /// <summary>Bu serviste bu adla kayıtlı event yok.</summary>
    Unknown,

    /// <summary>Inbox: bu tüketicide daha önce işlenmiş.</summary>
    Duplicate,
}

/// <summary>
/// Bellek içi taşıyıcı: zarfı aynı süreçte, hemen dağıtır (çağıran handler'lar bitene kadar bekler, hata yukarı çıkar).
/// Tek uygulama için yeterli; outbox ile birlikte kullanıldığında hata alan event outbox'ta tekrar denenir.
/// </summary>
internal sealed class InMemoryEventTransport : IEventTransport
{
    private readonly EventDispatcher _dispatcher;

    public InMemoryEventTransport(EventDispatcher dispatcher) => _dispatcher = dispatcher;

    public Task SendAsync(EventEnvelope envelope, CancellationToken cancellationToken = default) =>
        _dispatcher.DispatchAsync(envelope, cancellationToken);
}
