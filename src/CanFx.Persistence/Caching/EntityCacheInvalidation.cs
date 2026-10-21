using System.Data.Common;
using CanFx.Application;
using CanFx.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace CanFx.Persistence.Caching;

/// <summary>
/// Kaydedilen entity'lerin etiketlerini (<see cref="CacheTags.Entity(Type)"/>) kayıtlı tüm
/// <see cref="ICacheInvalidationTarget"/>'lara iletir: <c>entity:Product</c> etiketli handler ve HTTP önbellek kayıtları
/// bir ürün eklendiğinde, değiştiğinde ya da silindiğinde kendiliğinden düşer.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Açık bir transaction varsa silme commit'e kadar bekletilir; aksi halde başka bir istek commit'ten önce eski
/// veriyi okuyup tekrar önbelleğe yazabilirdi. Geri alınan transaction'da hiçbir şey silinmez.</item>
/// <item>Owned tiplerin değişikliği sahibinin etiketiyle bildirilir.</item>
/// <item>Altyapı tabloları (outbox, inbox, audit log) etiket üretmez.</item>
/// <item>Transaction EF dışından (doğrudan <see cref="DbTransaction"/> ile) commit edilirse silme yapılamaz; kayıtlar
/// süreleri dolunca düşer.</item>
/// </list>
/// </remarks>
public sealed class EntityCacheInvalidationInterceptor : SaveChangesInterceptor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly EntityCacheInvalidationState _state;
    private HashSet<string>? _saving;

    public EntityCacheInvalidationInterceptor(IServiceProvider serviceProvider, EntityCacheInvalidationState state)
    {
        _serviceProvider = serviceProvider;
        _state = state;
    }

    private bool IsSuspended => _serviceProvider.GetService<SaveChangesState>()?.IsWritingAuditTrail == true;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        AfterSaveAsync(eventData.Context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await AfterSaveAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        _saving = null;
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _saving = null;
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void Collect(DbContext? context)
    {
        if (context is null || IsSuspended)
            return;

        HashSet<string>? tags = null;
        foreach (EntityEntry entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;
            if (TagOf(entry.Metadata) is { } tag)
                (tags ??= new HashSet<string>(StringComparer.Ordinal)).Add(tag);
        }

        _saving = tags;
    }

    private async ValueTask AfterSaveAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (IsSuspended || _saving is not { Count: > 0 } tags)
            return;
        _saving = null;

        if (context?.Database.CurrentTransaction is not null)
        {
            _state.Defer(tags);
            return;
        }

        await _serviceProvider.GetServices<ICacheInvalidationTarget>().InvalidateTagsAsync(tags, cancellationToken).ConfigureAwait(false);
    }

    private static string? TagOf(IEntityType entityType)
    {
        // Owned tipin değişikliği sahibinin değişikliğidir
        IEntityType root = entityType;
        while (root.FindOwnership() is { } ownership)
            root = ownership.PrincipalEntityType;

        if (root.ClrType.Assembly == typeof(EntityCacheInvalidationInterceptor).Assembly)
            return null; // outbox, inbox, audit log

        return root.HasSharedClrType ? CacheTags.Entity(root.Name) : CacheTags.Entity(root.ClrType);
    }
}

/// <summary>Transaction içindeki kayıtların etiketlerini commit'e kadar bekletir.</summary>
public sealed class EntityCacheInvalidationTransactionInterceptor : DbTransactionInterceptor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly EntityCacheInvalidationState _state;

    public EntityCacheInvalidationTransactionInterceptor(IServiceProvider serviceProvider, EntityCacheInvalidationState state)
    {
        _serviceProvider = serviceProvider;
        _state = state;
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        FlushAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        base.TransactionCommitted(transaction, eventData);
    }

    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await base.TransactionCommittedAsync(transaction, eventData, cancellationToken).ConfigureAwait(false);
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        _state.Clear();
        base.TransactionRolledBack(transaction, eventData);
    }

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        _state.Clear();
        return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        _state.Clear();
        base.TransactionFailed(transaction, eventData);
    }

    public override Task TransactionFailedAsync(DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _state.Clear();
        return base.TransactionFailedAsync(transaction, eventData, cancellationToken);
    }

    private async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        if (_state.Take() is { Count: > 0 } tags)
            await _serviceProvider.GetServices<ICacheInvalidationTarget>().InvalidateTagsAsync(tags, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Scope'a ait, commit bekleyen etiketler.</summary>
public sealed class EntityCacheInvalidationState
{
    private HashSet<string>? _deferred;

    internal void Defer(IEnumerable<string> tags) => (_deferred ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(tags);

    internal HashSet<string>? Take()
    {
        HashSet<string>? tags = _deferred;
        _deferred = null;
        return tags;
    }

    internal void Clear() => _deferred = null;
}
