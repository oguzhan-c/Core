using Can.Core.Domain.Auditing;
using Can.Core.Domain.Entities;
using Can.Core.Domain.Events;
using Can.Core.Domain.ValueObjects;

namespace Can.Core.Domain.Tests;

public class EntityTests
{
    private sealed class Product : Entity<int>
    {
        public Product() { }

        public Product(int id)
            : base(id) { }
    }

    private sealed class Category : Entity<int>
    {
        public Category(int id)
            : base(id) { }
    }

    private readonly record struct OrderId(Guid Value);

    private sealed record OrderConfirmed(OrderId OrderId) : DomainEvent;

    private sealed class Order : FullAuditedAggregateRoot<OrderId>
    {
        public Order(OrderId id)
            : base(id) { }

        public bool IsConfirmed { get; private set; }

        public void Confirm()
        {
            IsConfirmed = true;
            RaiseDomainEvent(new OrderConfirmed(Id));
        }
    }

    private sealed class Money(decimal amount, string currency) : ValueObject
    {
        public decimal Amount { get; } = amount;
        public string Currency { get; } = currency;

        protected override IEnumerable<object?> GetEqualityComponents()
        {
            yield return Amount;
            yield return Currency;
        }
    }

    [Fact]
    public void IsSameAs_same_type_and_id_is_true()
    {
        Assert.True(new Product(5).IsSameAs(new Product(5)));
    }

    [Fact]
    public void IsSameAs_different_id_is_false()
    {
        Assert.False(new Product(5).IsSameAs(new Product(6)));
    }

    [Fact]
    public void IsSameAs_two_transient_entities_are_not_same()
    {
        var a = new Product();
        var b = new Product();

        Assert.True(a.IsTransient());
        Assert.False(a.IsSameAs(b));
        Assert.True(a.IsSameAs(a));
    }

    [Fact]
    public void IsSameAs_different_entity_types_with_same_id_is_false()
    {
        Entity<int> product = new Product(1);
        Entity<int> category = new Category(1);

        Assert.False(product.IsSameAs(category));
    }

    [Fact]
    public void Equals_is_reference_equality()
    {
        var a = new Product(5);
        var b = new Product(5);

        Assert.NotEqual<object>(a, b);
        Assert.Single(new HashSet<Product> { a, a });
    }

    [Fact]
    public void Strongly_typed_id_works()
    {
        var id = new OrderId(Guid.CreateVersion7());

        Assert.True(new Order(id).IsSameAs(new Order(id)));
        Assert.True(new Order(default).IsTransient());
    }

    [Fact]
    public void Aggregate_raises_and_clears_domain_events()
    {
        var order = new Order(new OrderId(Guid.CreateVersion7()));

        order.Confirm();

        IDomainEvent domainEvent = Assert.Single(order.DomainEvents);
        var confirmed = Assert.IsType<OrderConfirmed>(domainEvent);
        Assert.Equal(order.Id, confirmed.OrderId);
        Assert.NotEqual(Guid.Empty, confirmed.EventId);

        order.ClearDomainEvents();
        Assert.Empty(order.DomainEvents);
    }

    [Fact]
    public void Full_audited_aggregate_exposes_audit_interfaces()
    {
        var order = new Order(new OrderId(Guid.CreateVersion7()));

        Assert.IsAssignableFrom<IFullAudited>(order);
        Assert.IsAssignableFrom<IHasDomainEvents>(order);
        Assert.False(order.IsDeleted);
    }

    [Fact]
    public void ValueObject_equality_is_by_components()
    {
        var a = new Money(10, "TRY");
        var b = new Money(10, "TRY");
        var c = new Money(10, "EUR");

        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a != c);
        Assert.False(a.Equals(null));
    }
}
