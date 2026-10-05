using Can.Core.Domain.Events;
using Can.Core.Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Persistence.Interceptors;

/// <summary>
/// Aggregate'lerin domain event'lerini kayıt BAŞARILI olduktan sonra <see cref="IPublisher"/> ile yayınlar.
/// </summary>
/// <remarks>
/// <para>
/// Event'ler kayıttan önce toplanır (silinen aggregate'lerin event'leri de kaybolmasın diye) ama
/// aggregate'lerden ancak kayıt başarılı olunca temizlenir. Kayıt başarısız olursa event'ler
/// aggregate üzerinde kalır, hiçbir şey yayınlanmaz.
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

    public DomainEventInterceptor(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

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
        _pending.Clear();
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _pending.Clear();
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    // ----------------------------------------------------------------

    private void Collect(DbContext? context)
    {
        _pending.Clear();
        if (context is null)
            return;

        foreach (var entry in context.ChangeTracker.Entries<IHasDomainEvents>())
        {
            IHasDomainEvents source = entry.Entity;
            if (source.DomainEvents.Count > 0)
                _pending.Add((source, source.DomainEvents.ToArray()));
        }
    }

    private async Task PublishPendingAsync(CancellationToken cancellationToken)
    {
        if (_pending.Count == 0)
            return;

        // Handler'lar tekrar SaveChanges çağırabilir (iç içe); listeyi kopyalayıp hemen boşalt.
        var batch = _pending.ToArray();
        _pending.Clear();

        foreach ((IHasDomainEvents source, _) in batch)
            source.ClearDomainEvents();

        IPublisher publisher =
            _serviceProvider.GetService<IPublisher>()
            ?? throw new InvalidOperationException(
                "Domain event'leri yayınlamak için IPublisher kayıtlı olmalı. services.AddCanMediator(...) çağırdığından emin ol."
            );

        foreach ((_, IDomainEvent[] events) in batch)
        {
            foreach (IDomainEvent domainEvent in events)
                await publisher.Publish(domainEvent, cancellationToken).ConfigureAwait(false);
        }
    }
}
