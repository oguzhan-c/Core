using System.ComponentModel.DataAnnotations.Schema;
using CanFx.Domain.Events;

namespace CanFx.Security.Entities;

/// <summary>
/// Domain event yayınlayabilen kullanıcı: <see cref="User{TId}"/>'in her şeyi + <see cref="RaiseDomainEvent"/>. Event'ler
/// kayıt sırasında dağıtılır (CanFx.Persistence, outbox kuruluysa onunla). DDD ile çalışan projeler için; event
/// gerekmiyorsa <see cref="User{TId}"/> yeterli.
/// </summary>
/// <example>
/// <code>
/// public sealed class AppUser : UserAggregateRoot&lt;Guid&gt;
/// {
///     public string FirstName { get; private set; } = "";
///
///     public void Rename(string firstName)
///     {
///         FirstName = firstName;
///         RaiseDomainEvent(new UserRenamed(Id, firstName));
///     }
/// }
/// </code>
/// </example>
public class UserAggregateRoot<TId> : User<TId>, IHasDomainEvents
    where TId : notnull, IEquatable<TId>
{
    private readonly List<IDomainEvent> _domainEvents = [];

    protected UserAggregateRoot() { }

    /// <summary>Id'yi kendisi üreten kullanıcılar için (ör. <c>Guid.CreateVersion7()</c>).</summary>
    public UserAggregateRoot(TId id, string email, string? userName = null)
        : base(id, email, userName) { }

    [NotMapped] // veritabanına yazılmaz
    public IReadOnlyCollection<IDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    public void ClearDomainEvents() => _domainEvents.Clear();

    protected void RaiseDomainEvent(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        _domainEvents.Add(domainEvent);
    }
}
