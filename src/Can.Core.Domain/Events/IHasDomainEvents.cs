namespace Can.Core.Domain.Events;

/// <summary>
/// Domain event biriktiren nesneler. Persistence katmanındaki interceptor, SaveChanges sırasında
/// change tracker'daki tüm <see cref="IHasDomainEvents"/> nesnelerinden event'leri toplar,
/// kayıttan sonra yayınlar ve <see cref="ClearDomainEvents"/> çağırır.
/// </summary>
public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }

    void ClearDomainEvents();
}
