using System.Diagnostics;
using Can.Core.BackgroundJobs;
using Can.Core.Domain.Events;
using Can.Core.EventBus;
using Can.Core.Mediator;
using Can.Core.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Can.Core.Persistence.Outbox;

/// <summary>
/// Outbox'taki bekleyen mesajları yayınlayan tekrarlayan iş (<c>AddCanOutbox&lt;TContext&gt;()</c> ile kaydedilir).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Ortak veritabanını ve kendi veritabanı olan her aktif tenant'ın veritabanını tarar.</item>
/// <item>Her mesaj tek tek kilitlenerek alınır; birden fazla uygulama örneği aynı mesajı yayınlamaz.</item>
/// <item>Handler'lar mesajın tenant'ı adına, ayrı bir DI scope'unda çalışır.</item>
/// <item>Hata olursa mesaj sonraki turda tekrar denenir (<see cref="OutboxOptions.MaxAttempts"/> kadar).</item>
/// </list>
/// </remarks>
public sealed partial class OutboxProcessor<TContext> : IBackgroundJob
    where TContext : DbContext
{
    private const int MaxErrorLength = 2000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OutboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutboxProcessor<TContext>> _logger;

    public OutboxProcessor(
        IServiceScopeFactory scopeFactory,
        OutboxOptions options,
        TimeProvider timeProvider,
        ILogger<OutboxProcessor<TContext>> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    Task IBackgroundJob.ExecuteAsync(CancellationToken cancellationToken) => ProcessAsync(cancellationToken);

    /// <summary>Tüm veritabanlarındaki bekleyen mesajları bir tur işler; yayınlanan mesaj sayısını döndürür.</summary>
    public async Task<int> ProcessAsync(CancellationToken cancellationToken = default)
    {
        int published = await ProcessDatabaseAsync(databaseTenant: null, cancellationToken).ConfigureAwait(false);

        foreach (TenantInfo tenant in await GetDedicatedTenantsAsync(cancellationToken).ConfigureAwait(false))
            published += await ProcessDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false);

        return published;
    }

    private async Task<IReadOnlyList<TenantInfo>> GetDedicatedTenantsAsync(CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<ITenantStore>() is not { } store)
            return [];

        IReadOnlyList<TenantInfo> tenants = await store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return tenants
            .Where(t => t.IsActive && t.HasDedicatedDatabase)
            .DistinctBy(t => t.ConnectionString, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<int> ProcessDatabaseAsync(TenantInfo? databaseTenant, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        if (databaseTenant is not null)
            scope.ServiceProvider.GetRequiredService<TenantContext>().Set(databaseTenant);

        DbSet<OutboxMessage> messages = scope.ServiceProvider.GetRequiredService<TContext>().Set<OutboxMessage>();
        DateTime now = UtcNow();
        int maxAttempts = _options.MaxAttempts;

        List<Guid> candidates = await messages
            .AsNoTracking()
            .Where(m => m.ProcessedAt == null && m.Attempts < maxAttempts && (m.LockedUntil == null || m.LockedUntil < now))
            .OrderBy(m => m.OccurredAt)
            .Select(m => m.Id)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int published = 0;
        foreach (Guid id in candidates)
        {
            if (await TryPublishAsync(messages, id, databaseTenant, cancellationToken).ConfigureAwait(false))
                published++;
        }

        await DeleteOldMessagesAsync(messages, cancellationToken).ConfigureAwait(false);
        return published;
    }

    private async Task<bool> TryPublishAsync(DbSet<OutboxMessage> messages, Guid id, TenantInfo? databaseTenant, CancellationToken cancellationToken)
    {
        DateTime now = UtcNow();
        DateTime lockedUntil = now.Add(_options.LockDuration);

        // Kilitleme: yalnızca bu güncellemeyi başaran işlemci mesajı yayınlar.
        int claimed = await messages
            .Where(m => m.Id == id && m.ProcessedAt == null && (m.LockedUntil == null || m.LockedUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LockedUntil, lockedUntil), cancellationToken)
            .ConfigureAwait(false);

        if (claimed == 0)
            return false;

        OutboxMessage message = await messages.AsNoTracking().SingleAsync(m => m.Id == id, cancellationToken).ConfigureAwait(false);
        string eventName = ShortTypeName(message.Type);

        using Activity? activity = OutboxTelemetry.Source.StartActivity($"outbox {eventName}", ActivityKind.Producer);
        activity?.SetTag("can.event", eventName);
        activity?.SetTag("can.outbox.attempt", message.Attempts + 1);
        if (message.TenantId is not null)
            activity?.SetTag("can.tenant", message.TenantId);

        try
        {
            await DispatchAsync(message, databaseTenant, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            OutboxTelemetry.Messages.Add(1, new KeyValuePair<string, object?>("can.event", eventName), new KeyValuePair<string, object?>("can.outcome", "failed"));

            LogPublishFailed(exception, message.Id, message.Type, message.Attempts + 1);
            string error = exception.ToString();
            if (error.Length > MaxErrorLength)
                error = error[..MaxErrorLength];

            await messages
                .Where(m => m.Id == id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(m => m.Attempts, m => m.Attempts + 1)
                        .SetProperty(m => m.LastError, error)
                        .SetProperty(m => m.LockedUntil, (DateTime?)null),
                    CancellationToken.None
                )
                .ConfigureAwait(false);
            return false;
        }

        OutboxTelemetry.Messages.Add(1, new KeyValuePair<string, object?>("can.event", eventName), new KeyValuePair<string, object?>("can.outcome", "published"));
        DateTime processedAt = UtcNow();
        await messages
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.ProcessedAt, processedAt)
                    .SetProperty(m => m.LockedUntil, (DateTime?)null)
                    .SetProperty(m => m.LastError, (string?)null),
                CancellationToken.None
            )
            .ConfigureAwait(false);
        return true;
    }

    private async Task DispatchAsync(OutboxMessage message, TenantInfo? databaseTenant, CancellationToken cancellationToken)
    {
        IIntegrationEvent integrationEvent = message.Deserialize();

        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        IServiceProvider services = scope.ServiceProvider;

        if (services.GetService<TenantContext>() is { } tenantContext)
        {
            if (databaseTenant is not null)
            {
                tenantContext.Set(databaseTenant);
            }
            else if (message.TenantId is not null)
            {
                TenantInfo? tenant = services.GetService<ITenantStore>() is { } store
                    ? await store.FindAsync(message.TenantId, cancellationToken).ConfigureAwait(false)
                    : null;

                if (tenant is not null)
                    tenantContext.Set(tenant);
                else
                    tenantContext.Set(message.TenantId);
            }
        }

        // Event bus kayıtlıysa (AddCanEventBus) taşıyıcıya gider; değilse bu süreçteki handler'lara yayınlanır.
        if (services.GetService<IEventBus>() is { } eventBus)
            await eventBus.PublishAsync(integrationEvent, cancellationToken).ConfigureAwait(false);
        else
            await services.GetRequiredService<IPublisher>().Publish(integrationEvent, cancellationToken).ConfigureAwait(false);
    }

    private async Task DeleteOldMessagesAsync(DbSet<OutboxMessage> messages, CancellationToken cancellationToken)
    {
        if (_options.RetainProcessedMessagesFor is not { } retain)
            return;

        DateTime threshold = UtcNow().Subtract(retain);
        await messages.Where(m => m.ProcessedAt != null && m.ProcessedAt < threshold).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

    /// <summary><c>Namespace.Tip, Assembly</c> → <c>Tip</c> (etiketlerde kısa ad).</summary>
    private static string ShortTypeName(string typeName)
    {
        string name = typeName.Split(',')[0];
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[(dot + 1)..];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Outbox mesajı {MessageId} ({EventType}) yayınlanamadı; deneme {Attempt}.")]
    private partial void LogPublishFailed(Exception exception, Guid messageId, string eventType, int attempt);
}
