using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Can.Core.Application;
using Can.Core.Domain.Auditing;
using Can.Core.MultiTenancy;
using Can.Core.Persistence.Interceptors;
using Can.Core.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Persistence.AuditTrail;

/// <summary>
/// Denetlenen entity'lerin değişikliklerini <see cref="AuditLog"/> olarak yazar. Model'de audit trail yoksa
/// (<c>modelBuilder.AddCanAuditTrail()</c> çağrılmadıysa) hiçbir şey yapmaz.
/// </summary>
/// <remarks>
/// Geçmiş, değişiklikle aynı SaveChanges içinde yazılır. Anahtarı veritabanında üretilen YENİ kayıtların
/// (ör. identity int) geçmişi ise anahtar belli olunca ikinci bir SaveChanges ile yazılır; ikisinin birlikte
/// geri alınabilmesi için transaction kullan (<c>ITransactionalRequest</c> ya da <c>IUnitOfWork.ExecuteInTransactionAsync</c>).
/// Owned tiplerin değişiklikleri ayrıca kaydedilmez.
/// </remarks>
public sealed class AuditTrailInterceptor : SaveChangesInterceptor
{
    private static readonly ConcurrentDictionary<(Type, bool), bool> AuditedTypes = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTenant _currentTenant;
    private readonly TimeProvider _timeProvider;
    private readonly AuditTrailOptions _options;
    private readonly IServiceProvider _serviceProvider;

    private readonly List<(DbContext Context, AuditLog Log)> _added = [];
    private readonly List<(AuditLog Log, EntityEntry Entry)> _waitingForKeys = [];

    public AuditTrailInterceptor(
        ICurrentUser currentUser,
        ICurrentTenant currentTenant,
        TimeProvider timeProvider,
        AuditTrailOptions options,
        IServiceProvider serviceProvider)
    {
        _currentUser = currentUser;
        _currentTenant = currentTenant;
        _timeProvider = timeProvider;
        _options = options;
        _serviceProvider = serviceProvider;
    }

    private SaveChangesState State => _serviceProvider.GetRequiredService<SaveChangesState>();

    // ---------------------------------------------------------------- kayıttan önce

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Capture(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Capture(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // ---------------------------------------------------------------- kayıttan sonra: anahtarı yeni belli olanlar

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        if (TakeWaitingLogs(eventData.Context) is { } context)
        {
            State.IsWritingAuditTrail = true;
            try
            {
                context.SaveChanges();
            }
            finally
            {
                State.IsWritingAuditTrail = false;
            }
        }

        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (TakeWaitingLogs(eventData.Context) is { } context)
        {
            State.IsWritingAuditTrail = true;
            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                State.IsWritingAuditTrail = false;
            }
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- hata

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        Reset();
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Reset();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    // ----------------------------------------------------------------

    private void Capture(DbContext? context)
    {
        if (State.IsWritingAuditTrail)
            return;

        // Önceki kayıt tamamlanmadıysa (ör. eşzamanlılık hatası) eklediği geçmiş kayıtları geri alınır.
        Reset();

        if (context is null || context.Model.FindEntityType(typeof(AuditLog)) is null)
            return;

        context.ChangeTracker.DetectChanges();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        string? userId = _currentUser.Id;
        string? tenantId = _serviceProvider.GetService<TenantContext>()?.TenantId ?? _currentTenant.Id?.ToString();
        string? traceId = Activity.Current?.TraceId.ToString();

        foreach (EntityEntry entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is AuditLog or OutboxMessage || entry.Metadata.IsOwned() || !IsAudited(entry.Metadata.ClrType))
                continue;

            AuditAction? action = GetAction(entry);
            if (action is null)
                continue;

            Dictionary<string, AuditPropertyChange> changes = GetChanges(entry, action.Value);
            if (action == AuditAction.Updated && changes.Count == 0)
                continue;

            bool keyIsTemporary = entry.Properties.Any(p => p.Metadata.IsPrimaryKey() && p.IsTemporary);
            var log = new AuditLog(
                entry.Metadata.ClrType.Name,
                keyIsTemporary ? string.Empty : GetEntityId(entry),
                action.Value,
                changes.Count == 0 ? null : JsonSerializer.Serialize(changes, JsonOptions),
                userId,
                tenantId,
                now,
                traceId
            );

            if (keyIsTemporary)
            {
                _waitingForKeys.Add((log, entry));
            }
            else
            {
                context.Add(log);
                _added.Add((context, log));
            }
        }
    }

    private DbContext? TakeWaitingLogs(DbContext? context)
    {
        if (State.IsWritingAuditTrail)
            return null;

        _added.Clear();
        if (context is null || _waitingForKeys.Count == 0)
            return null;

        // İç içe SaveChanges'ten önce listeyi boşalt.
        var waiting = _waitingForKeys.ToArray();
        _waitingForKeys.Clear();

        foreach ((AuditLog log, EntityEntry entry) in waiting)
        {
            log.EntityId = GetEntityId(entry);
            context.Add(log);
        }

        return context;
    }

    private void Reset()
    {
        if (State.IsWritingAuditTrail)
            return;

        foreach ((DbContext context, AuditLog log) in _added)
        {
            EntityEntry entry = context.Entry(log);
            if (entry.State == EntityState.Added)
                entry.State = EntityState.Detached;
        }

        _added.Clear();
        _waitingForKeys.Clear();
    }

    // ---------------------------------------------------------------- yardımcılar

    private bool IsAudited(Type type) =>
        AuditedTypes.GetOrAdd(
            (type, _options.AuditAllEntities),
            static key =>
                !key.Item1.IsDefined(typeof(DisableAuditingAttribute), inherit: true)
                && (key.Item2 || key.Item1.IsDefined(typeof(AuditedAttribute), inherit: true))
        );

    private static AuditAction? GetAction(EntityEntry entry) =>
        entry.State switch
        {
            EntityState.Added => AuditAction.Created,
            EntityState.Deleted => AuditAction.Deleted,
            EntityState.Modified when IsSoftDeleted(entry) => AuditAction.Deleted,
            EntityState.Modified => AuditAction.Updated,
            _ => null,
        };

    private static bool IsSoftDeleted(EntityEntry entry)
    {
        if (entry.Entity is not ISoftDeletable)
            return false;

        PropertyEntry isDeleted = entry.Property(nameof(ISoftDeletable.IsDeleted));
        return isDeleted.IsModified && isDeleted.CurrentValue is true && isDeleted.OriginalValue is false;
    }

    private Dictionary<string, AuditPropertyChange> GetChanges(EntityEntry entry, AuditAction action)
    {
        var changes = new Dictionary<string, AuditPropertyChange>(StringComparer.Ordinal);

        foreach (PropertyEntry property in entry.Properties)
        {
            if (property.Metadata.IsPrimaryKey() || IsIgnored(property))
                continue;

            switch (action)
            {
                case AuditAction.Created:
                    changes[property.Metadata.Name] = new AuditPropertyChange(null, property.CurrentValue);
                    break;

                case AuditAction.Deleted when entry.State == EntityState.Deleted:
                    changes[property.Metadata.Name] = new AuditPropertyChange(property.OriginalValue, null);
                    break;

                default:
                    if (property.IsModified && !Equals(property.OriginalValue, property.CurrentValue))
                        changes[property.Metadata.Name] = new AuditPropertyChange(property.OriginalValue, property.CurrentValue);
                    break;
            }
        }

        return changes;
    }

    private bool IsIgnored(PropertyEntry property) =>
        _options.IgnoredProperties.Contains(property.Metadata.Name)
        || property.Metadata.PropertyInfo?.IsDefined(typeof(DisableAuditingAttribute), inherit: true) == true;

    private static string GetEntityId(EntityEntry entry)
    {
        IEnumerable<object?> values = entry.Metadata.FindPrimaryKey()?.Properties.Select(p => entry.Property(p.Name).CurrentValue) ?? [];
        return string.Join(",", values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)));
    }

    private sealed record AuditPropertyChange(object? Old, object? New);
}
