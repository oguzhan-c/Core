using Can.Core.Application;
using Can.Core.Domain.Events;
using Can.Core.Mediator;
using Can.Core.MultiTenancy;
using Can.Core.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Persistence.Interceptors;

/// <summary>
/// Aggregate'lerin domain event'lerini kayıt BAŞARILI olduktan sonra <see cref="IPublisher"/> ile yayınlar;
/// <see cref="IIntegrationEvent"/>'leri ise aynı kayıtta outbox tablosuna yazar.
/// </summary>
/// <remarks>
/// <para>
/// Event'ler kayıttan önce toplanır (silinen aggregate'lerin event'leri de kaybolmasın diye) ama
/// aggregate'lerden ancak kayıt başarılı olunca temizlenir. Kayıt başarısız olursa event'ler
/// aggregate üzerinde kalır, hiçbir şey yayınlanmaz ve outbox'a eklenen mesajlar geri alınır.
/// </para>
/// <para>
/// Handler'lar aynı DI scope'unda (aynı DbContext ile) çalışır. Bir handler başka bir aggregate'i
/// değiştirip tekrar SaveChanges çağırırsa onun event'leri de aynı şekilde yayınlanır. Açık bir
/// transaction varsa handler'ların değişiklikleri de o transaction'a dahildir.
/// </para>
/// <para>
/// Bu interceptor scoped'tır ve DbContext havuzlama (<c>AddDbContextPool</c>) ile kullanılmamalıdır.
/// </para>
/// </remarks>
public sealed class DomainEventInterceptor : SaveChangesInterceptor
{
    private readonly IServiceProvider _serviceProvider;
    private readonly List<(IHasDomainEvents Source, IDomainEvent[] Events)> _pending = [];
    private readonly List<(DbContext Context, OutboxMessage Message)> _pendingOutbox = [];

    public DomainEventInterceptor(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    private bool IsSuspended => _serviceProvider.GetService<SaveChangesState>()?.IsWritingAuditTrail == true;

    // ---------------------------------------------------------------- Kayıttan önce: topla

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // ---------------------------------------------------------------- Kayıttan sonra: yayınla

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        PublishPendingAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SavedChanges(eventData, result);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        await PublishPendingAsync(cancellationToken).ConfigureAwait(false);
        return await base.SavedChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- Hata: event'ler aggregate'te kalsın

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

    private void Collect(DbContext? context)
    {
        if (IsSuspended)
            return;

        // Önceki kayıt tamamlanmadıysa (ör. eşzamanlılık hatası) eklediği outbox mesajları geri alınır.
        Reset();
        if (context is null)
            return;

        foreach (var entry in context.ChangeTracker.Entries<IHasDomainEvents>().ToList())
        {
            IHasDomainEvents source = entry.Entity;
            if (source.DomainEvents.Count > 0)
                _pending.Add((source, source.DomainEvents.ToArray()));
        }

        AddIntegrationEventsToOutbox(context);
    }

    private void AddIntegrationEventsToOutbox(DbContext context)
    {
        IIntegrationEvent[] integrationEvents = _pending.SelectMany(p => p.Events).OfType<IIntegrationEvent>().ToArray();
        if (integrationEvents.Length == 0)
            return;

        if (context.Model.FindEntityType(typeof(OutboxMessage)) is null)
        {
            _pending.Clear();
            throw new InvalidOperationException(
                $"'{integrationEvents[0].GetType().Name}' bir IIntegrationEvent ama DbContext modelinde outbox yok. "
                    + "ConfigureModel içinde modelBuilder.AddCanOutbox() çağır ve services.AddCanOutbox<TContext>() ile işlemciyi kaydet."
            );
        }

        string? tenantId = _serviceProvider.GetService<TenantContext>()?.TenantId ?? _serviceProvider.GetService<ICurrentTenant>()?.Id?.ToString();

        foreach (IIntegrationEvent integrationEvent in integrationEvents)
        {
            OutboxMessage message = OutboxMessage.Create(integrationEvent, tenantId);
            context.Add(message);
            _pendingOutbox.Add((context, message));
        }
    }

    private void Reset()
    {
        if (IsSuspended)
            return;

        _pending.Clear();

        foreach ((DbContext context, OutboxMessage message) in _pendingOutbox)
        {
            var entry = context.Entry(message);
            if (entry.State == EntityState.Added)
                entry.State = EntityState.Detached;
        }

        _pendingOutbox.Clear();
    }

    private async Task PublishPendingAsync(CancellationToken cancellationToken)
    {
        if (IsSuspended)
            return;

        _pendingOutbox.Clear();
        if (_pending.Count == 0)
            return;

        // Handler'lar tekrar SaveChanges çağırabilir (iç içe); listeyi kopyalayıp hemen boşalt.
        var batch = _pending.ToArray();
        _pending.Clear();

        foreach ((IHasDomainEvents source, _) in batch)
            source.ClearDomainEvents();

        IDomainEvent[] events = batch.SelectMany(p => p.Events).Where(e => e is not IIntegrationEvent).ToArray();
        if (events.Length == 0)
            return;

        IPublisher publisher =
            _serviceProvider.GetService<IPublisher>()
            ?? throw new InvalidOperationException(
                "Domain event'leri yayınlamak için IPublisher kayıtlı olmalı. services.AddCanMediator(...) çağırdığından emin ol."
            );

        foreach (IDomainEvent domainEvent in events)
            await publisher.Publish((object)domainEvent, cancellationToken).ConfigureAwait(false);
    }
}
