using Can.Core.Domain.Exceptions;
using Northwind.Domain.Catalog;
using Northwind.Domain.Common;
using Northwind.Domain.Customers;
using Northwind.Domain.Orders;

namespace Northwind.Domain.Tests;

public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Address Address = new("Obere Str. 57", "Berlin", null, "12209", "Germany");

    private static Product Chai(int stock = 10, int reorderLevel = 2) =>
        Product.Create("Chai", null, null, "10 boxes x 20 bags", 18m, stock, reorderLevel);

    private static Order NewOrder() => Order.Place(10249, Guid.NewGuid(), null, "Alfreds Futterkiste", Address, null, 5m, Now);

    [Fact]
    public void Adding_a_line_reserves_stock_and_merges_same_product()
    {
        Product chai = Chai(stock: 10);
        Order order = NewOrder();

        order.AddLine(chai, 3, 0.1m);
        order.AddLine(chai, 2);

        OrderLine line = Assert.Single(order.Lines);
        Assert.Equal(5, line.Quantity);
        Assert.Equal(5, chai.UnitsInStock);
        Assert.Equal(81m, line.LineTotal); // 18 * 5 * 0.9
        Assert.Equal(86m, order.Total); // + 5 kargo
        Assert.IsType<OrderPlaced>(Assert.Single(order.DomainEvents));
    }

    [Fact]
    public void Insufficient_or_discontinued_stock_is_rejected()
    {
        Product chai = Chai(stock: 2);
        Order order = NewOrder();

        Assert.Throws<BusinessException>(() => order.AddLine(chai, 3));
        Assert.Equal(2, chai.UnitsInStock);

        chai.Discontinue();
        Assert.Throws<BusinessException>(() => order.AddLine(chai, 1));
    }

    [Fact]
    public void Reserving_down_to_reorder_level_raises_event()
    {
        Product chai = Chai(stock: 5, reorderLevel: 3);

        NewOrder().AddLine(chai, 2);

        var lowStock = Assert.IsType<StockBelowReorderLevel>(Assert.Single(chai.DomainEvents));
        Assert.Equal(3, lowStock.UnitsInStock);
    }

    [Fact]
    public void Shipping_raises_integration_event_and_locks_the_order()
    {
        Order order = NewOrder();
        order.AddLine(Chai(), 1);
        Guid shipper = Guid.NewGuid();

        order.Ship(shipper, Now.AddDays(1));

        Assert.Equal(OrderStatus.Shipped, order.Status);
        var shipped = Assert.IsType<OrderShipped>(order.DomainEvents.Last());
        Assert.Equal(shipper, shipped.ShipperId);
        Assert.Equal(23m, shipped.Total);
        Assert.Throws<BusinessException>(() => order.Cancel());
        Assert.Throws<BusinessException>(() => order.AddLine(Chai(), 1));
    }

    [Fact]
    public void Empty_order_cannot_be_shipped()
    {
        Assert.Throws<BusinessException>(() => NewOrder().Ship(Guid.NewGuid(), Now));
    }

    [Fact]
    public void Cancelling_reports_quantities_to_release()
    {
        Product chai = Chai();
        Order order = NewOrder();
        order.AddLine(chai, 4);

        order.Cancel();
        order.Cancel(); // ikinci kez: etkisiz

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        var cancelled = Assert.Single(order.DomainEvents.OfType<OrderCancelled>());
        Assert.Equal(new OrderedQuantity(chai.Id, 4), Assert.Single(cancelled.Lines));
    }

    [Fact]
    public void Discount_must_be_between_zero_and_one()
    {
        Assert.Throws<BusinessException>(() => NewOrder().AddLine(Chai(), 1, 1.5m));
    }
}

public class CatalogTests
{
    [Fact]
    public void Price_change_raises_event_only_when_price_changes()
    {
        Product product = Product.Create("Chang", null, null, null, 19m, 17, 25);

        product.ChangePrice(19m);
        Assert.Empty(product.DomainEvents);

        product.ChangePrice(21.5m);
        var changed = Assert.IsType<ProductPriceChanged>(Assert.Single(product.DomainEvents));
        Assert.Equal((19m, 21.5m), (changed.OldPrice, changed.NewPrice));
    }

    [Fact]
    public void Discontinue_is_idempotent_and_raises_integration_event_once()
    {
        Product product = Product.Create("Chang", null, null, null, 19m, 17, 25);

        product.Discontinue();
        product.Discontinue();

        Assert.True(product.IsDiscontinued);
        Assert.IsType<ProductDiscontinued>(Assert.Single(product.DomainEvents));
    }

    [Fact]
    public void Needs_reorder_counts_units_on_order()
    {
        Product product = Product.Import("Aniseed Syrup", null, null, null, 10m, 13, 70, 25, false);
        Assert.False(product.NeedsReorder);

        Product low = Product.Import("Chef Anton's Gumbo Mix", null, null, null, 21.35m, 0, 0, 0, false);
        Assert.True(low.NeedsReorder);
    }

    [Theory]
    [InlineData("alfki", "ALFKI")]
    [InlineData(" anatr ", "ANATR")]
    public void Customer_code_is_normalized(string code, string expected)
    {
        Assert.Equal(expected, Customer.NormalizeCode(code));
    }

    [Theory]
    [InlineData("")]
    [InlineData("TOOLONG")]
    [InlineData("A-B")]
    public void Invalid_customer_code_is_rejected(string code)
    {
        Assert.Throws<BusinessException>(() => Customer.NormalizeCode(code));
    }

    [Fact]
    public void Address_requires_street_city_and_country_and_compares_by_value()
    {
        Assert.Throws<BusinessException>(() => new Address("", "Berlin", null, null, "Germany"));
        Assert.Equal(new Address("A", "B", null, "1", "C"), new Address("A ", "B", "", "1", "C"));
    }
}
