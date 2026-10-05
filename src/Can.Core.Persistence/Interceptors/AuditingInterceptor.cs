using System.Collections.Concurrent;
using Can.Core.Application;
using Can.Core.Domain.Auditing;
using Can.Core.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Can.Core.Persistence.Interceptors;

/// <summary>
/// SaveChanges sırasında:
/// <list type="bullet">
/// <item>Yeni kayıtlara <c>CreatedAt/CreatedBy</c> ve aktif tenant'ı yazar.</item>
/// <item>Güncellenen kayıtlara <c>UpdatedAt/UpdatedBy</c> yazar; oluşturma bilgisinin ve tenant'ın değiştirilmesini engeller.</item>
/// <item><c>ISoftDeletable</c> kayıtların silinmesini işaretlemeye çevirir (<c>IsDeleted</c>, <c>DeletedAt/DeletedBy</c>).</item>
/// </list>
/// Değerler EF Core'un <c>EntityEntry</c> API'si üzerinden yazılır; böylece değişiklik takibi kaçırılmaz.
/// </summary>
public sealed class AuditingInterceptor : SaveChangesInterceptor
{
    private static readonly ConcurrentDictionary<Type, Type?> TenantIdTypes = new();

    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTenant _currentTenant;
    private readonly TimeProvider _timeProvider;

    public AuditingInterceptor(ICurrentUser currentUser, ICurrentTenant currentTenant, TimeProvider timeProvider)
    {
        _currentUser = currentUser;
        _currentTenant = currentTenant;
        _timeProvider = timeProvider;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null)
            ApplyAuditing(eventData.Context);

        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
            ApplyAuditing(eventData.Context);

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void ApplyAuditing(DbContext context)
    {
        context.ChangeTracker.DetectChanges();

        DateTimeOffset now = _timeProvider.GetUtcNow();
        string? userId = _currentUser.Id;

        foreach (EntityEntry entry in context.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    SetCreationInfo(entry, now, userId);
                    SetTenant(entry);
                    break;

                case EntityState.Modified:
                    ProtectCreationInfo(entry);
                    SetModificationInfo(entry, now, userId);
                    SetDeletionInfoIfSoftDeleted(entry, now, userId);
                    break;

                case EntityState.Deleted when entry.Entity is ISoftDeletable && !PermanentDeletion.IsMarkedPermanent(entry.Entity):
                    ConvertToSoftDelete(entry, now, userId);
                    break;

                case EntityState.Deleted:
                    PermanentDeletion.Unmark(entry.Entity);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- Oluşturma / güncelleme

    private static void SetCreationInfo(EntityEntry entry, DateTimeOffset now, string? userId)
    {
        if (entry.Entity is not ICreationAudited)
            return;

        entry.Property(nameof(ICreationAudited.CreatedAt)).CurrentValue = now;
        entry.Property(nameof(ICreationAudited.CreatedBy)).CurrentValue = userId;
    }

    private static void ProtectCreationInfo(EntityEntry entry)
    {
        if (entry.Entity is ICreationAudited)
        {
            entry.Property(nameof(ICreationAudited.CreatedAt)).IsModified = false;
            entry.Property(nameof(ICreationAudited.CreatedBy)).IsModified = false;
        }

        // Kayıt başka bir tenant'a taşınamaz.
        if (GetTenantIdType(entry) is not null)
            entry.Property(TenantIdProperty).IsModified = false;
    }

    private static void SetModificationInfo(EntityEntry entry, DateTimeOffset now, string? userId)
    {
        if (entry.Entity is not IModificationAudited)
            return;

        entry.Property(nameof(IModificationAudited.UpdatedAt)).CurrentValue = now;
        entry.Property(nameof(IModificationAudited.UpdatedBy)).CurrentValue = userId;
    }

    // ---------------------------------------------------------------- Soft delete

    /// <summary>Repository'nin Delete metodu IsDeleted'ı true yapar; silme bilgisini burada tamamlarız.</summary>
    private static void SetDeletionInfoIfSoftDeleted(EntityEntry entry, DateTimeOffset now, string? userId)
    {
        if (entry.Entity is not ISoftDeletable softDeletable)
            return;

        PropertyEntry isDeleted = entry.Property(nameof(ISoftDeletable.IsDeleted));
        if (!isDeleted.IsModified || Equals(isDeleted.OriginalValue, isDeleted.CurrentValue))
            return;

        if (softDeletable.IsDeleted)
        {
            entry.Property(nameof(ISoftDeletable.DeletedAt)).CurrentValue = now;
            entry.Property(nameof(ISoftDeletable.DeletedBy)).CurrentValue = userId;
        }
        else
        {
            // Geri alma (restore)
            entry.Property(nameof(ISoftDeletable.DeletedAt)).CurrentValue = null;
            entry.Property(nameof(ISoftDeletable.DeletedBy)).CurrentValue = null;
        }
    }

    /// <summary>Doğrudan <c>DbContext.Remove</c> ile silinen kaydı işaretlemeye çevirir.</summary>
    private void ConvertToSoftDelete(EntityEntry entry, DateTimeOffset now, string? userId)
    {
        entry.State = EntityState.Modified;

        entry.Property(nameof(ISoftDeletable.IsDeleted)).CurrentValue = true;
        entry.Property(nameof(ISoftDeletable.DeletedAt)).CurrentValue = now;
        entry.Property(nameof(ISoftDeletable.DeletedBy)).CurrentValue = userId;

        ProtectCreationInfo(entry);
        SetModificationInfo(entry, now, userId);
        KeepOwnedEntities(entry);
    }

    /// <summary>Sahibi silinince EF owned tipleri de silinmiş işaretler; sahibi korunduğu için onları da geri al.</summary>
    private static void KeepOwnedEntities(EntityEntry entry)
    {
        foreach (NavigationEntry navigation in entry.Navigations)
        {
            if (!navigation.Metadata.TargetEntityType.IsOwned())
                continue;

            IEnumerable<object> targets = navigation switch
            {
                ReferenceEntry reference when reference.CurrentValue is not null => [reference.CurrentValue],
                CollectionEntry collection when collection.CurrentValue is not null => collection.CurrentValue.Cast<object>(),
                _ => [],
            };

            foreach (object target in targets)
            {
                EntityEntry ownedEntry = entry.Context.Entry(target);
                if (ownedEntry.State == EntityState.Deleted)
                {
                    ownedEntry.State = EntityState.Modified;
                    KeepOwnedEntities(ownedEntry);
                }
            }
        }
    }

    // ---------------------------------------------------------------- Tenant

    private const string TenantIdProperty = "TenantId";

    private void SetTenant(EntityEntry entry)
    {
        Type? tenantIdType = GetTenantIdType(entry);
        if (tenantIdType is null)
            return;

        PropertyEntry property = entry.Property(TenantIdProperty);
        object? current = _currentTenant.Id;
        bool hasValue = property.CurrentValue is { } value && !IsDefault(value, tenantIdType);

        if (!hasValue)
        {
            if (current is not null && tenantIdType.IsInstanceOfType(current))
                property.CurrentValue = current;

            return;
        }

        // Aktif tenant varken başka bir tenant'a kayıt yazılamaz.
        if (current is not null && !Equals(property.CurrentValue, current))
        {
            throw new InvalidOperationException(
                $"'{entry.Metadata.ClrType.Name}' kaydı aktif tenant ({current}) dışında bir tenant'a ({property.CurrentValue}) yazılamaz."
            );
        }
    }

    private static Type? GetTenantIdType(EntityEntry entry) =>
        TenantIdTypes.GetOrAdd(entry.Metadata.ClrType, CanDbContext.GetTenantIdType);

    private static bool IsDefault(object value, Type type) =>
        type.IsValueType && value.Equals(Activator.CreateInstance(type));
}
