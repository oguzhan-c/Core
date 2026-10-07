using Can.Core.Domain.Results;
using Northwind.Domain.Catalog;
using Northwind.Domain.Common;
using Northwind.Domain.Customers;
using Northwind.Domain.Orders;

namespace Northwind.Domain.Tests;

public class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Address Address = Address.Create("Obere Str. 57", "Berlin", null, "12209", "Germany").Value;

    private static Product Chai(int stock = 10, int reorderLevel = 2)
    {
        Product product = Product.Create("Chai", null, null, "10 boxes x 20 bags", 18m, stock, reorderLevel).Value;
        product.ClearDomainEvents(); // oluşturma event'i (ProductCatalogChanged) bu testlerin konusu değil
        return product;
    }

    private static Order NewOrder() => Order.Place(10249, Guid.NewGuid(), null, "Alfreds Futterkiste", Address, null, 5m, Now).Value;

    [Fact]
    public void Adding_a_line_reserves_stock_and_merges_same_product()
    {
        Product chai = Chai(stock: 10);
        Order order = NewOrder();

        Assert.True(order.AddLine(chai, 3, 0.1m).IsSuccess);
        Assert.True(order.AddLine(chai, 2).IsSuccess);

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

        Result<Success> tooMany = order.AddLine(chai, 3);
        Assert.Equal("product.insufficient_stock", tooMany.FirstError.Code);
        Assert.Equal(2, tooMany.FirstError.Metadata["inStock"]);
        Assert.Equal(2, chai.UnitsInStock);
        Assert.Empty(order.Lines);

        chai.Discontinue();
        Assert.Equal("product.discontinued", order.AddLine(chai, 1).FirstError.Code);
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

        Assert.True(order.Ship(shipper, Now.AddDays(1)).IsSuccess);

        Assert.Equal(OrderStatus.Shipped, order.Status);
        var shipped = Assert.IsType<OrderShipped>(order.DomainEvents.Last());
        Assert.Equal(shipper, shipped.ShipperId);
        Assert.Equal(23m, shipped.Total);
        Assert.Equal(OrderErrors.AlreadyShipped, order.Cancel().FirstError);
        Assert.Equal(OrderErrors.NotEditable, order.AddLine(Chai(), 1).FirstError);
    }

    [Fact]
    public void Empty_order_cannot_be_shipped()
    {
        Assert.Equal(OrderErrors.Empty, NewOrder().Ship(Guid.NewGuid(), Now).FirstError);
    }

    [Fact]
    public void Cancelling_reports_quantities_to_release()
    {
        Product chai = Chai();
        Order order = NewOrder();
        order.AddLine(chai, 4);

        Assert.True(order.Cancel().IsSuccess);
        Assert.True(order.Cancel().IsSuccess); // ikinci kez: etkisiz

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        var cancelled = Assert.Single(order.DomainEvents.OfType<OrderCancelled>());
        Assert.Equal(new OrderedQuantity(chai.Id, 4), Assert.Single(cancelled.Lines));
    }

    [Fact]
    public void Discount_must_be_between_zero_and_one()
    {
        Assert.Equal(OrderErrors.InvalidDiscount, NewOrder().AddLine(Chai(), 1, 1.5m).FirstError);
    }

    [Fact]
    public void Placing_collects_all_field_errors()
    {
        Result<Order> result = Order.Place(0, Guid.NewGuid(), null, "", Address, null, -1m, Now);

        Assert.Equal(3, result.Errors.Length);
        Assert.All(result.Errors, e => Assert.Equal(ErrorType.Validation, e.Type));
    }
}

public class CatalogTests
{
    [Fact]
    public void Price_change_raises_event_only_when_price_changes()
    {
        Product product = Product.Create("Chang", null, null, null, 19m, 17, 25).Value;
        product.ClearDomainEvents();

        product.ChangePrice(19m);
        Assert.Empty(product.DomainEvents);

        product.ChangePrice(21.5m);
        var changed = Assert.Single(product.DomainEvents.OfType<ProductPriceChanged>());
        Assert.Equal((19m, 21.5m), (changed.OldPrice, changed.NewPrice));

        Assert.Equal("not_negative", product.ChangePrice(-1m).FirstError.Code);
        Assert.Equal(21.5m, product.UnitPrice);
    }

    [Fact]
    public void Discontinue_is_idempotent_and_raises_integration_event_once()
    {
        Product product = Product.Create("Chang", null, null, null, 19m, 17, 25).Value;

        product.Discontinue();
        product.Discontinue();

        Assert.True(product.IsDiscontinued);
        Assert.Single(product.DomainEvents.OfType<ProductDiscontinued>());
    }

    [Fact]
    public void Catalog_changes_are_announced_for_the_search_index()
    {
        Product product = Product.Create("Chang", null, null, null, 19m, 17, 25).Value;
        Assert.IsType<ProductCatalogChanged>(Assert.Single(product.DomainEvents)); // oluşturma
        product.ClearDomainEvents();

        product.Restock(5);
        Assert.Empty(product.DomainEvents); // stok katalog bilgisi değil

        product.UpdateDetails("Chang Birası", null, null, null, 25);
        product.ChangePrice(20m);
        product.Discontinue();
        product.Remove();

        Assert.Equal(4, product.DomainEvents.OfType<ProductCatalogChanged>().Count());
        Assert.All(product.DomainEvents.OfType<ProductCatalogChanged>(), e => Assert.Equal(product.Id, e.ProductId));
    }

    [Fact]
    public void Needs_reorder_counts_units_on_order()
    {
        Product product = Product.Import("Aniseed Syrup", null, null, null, 10m, 13, 70, 25, false).Value;
        Assert.False(product.NeedsReorder);

        Product low = Product.Import("Chef Anton's Gumbo Mix", null, null, null, 21.35m, 0, 0, 0, false).Value;
        Assert.True(low.NeedsReorder);
    }

    [Fact]
    public void Invalid_product_returns_errors_instead_of_throwing()
    {
        Result<Product> result = Product.Create("", null, null, null, -1m, 0, 0);

        Assert.True(result.IsFailure);
        Assert.Contains(result.Errors, e => e.Field == "Ürün adı" || e.Field == "Birim fiyat");
    }

    [Theory]
    [InlineData("alfki", "ALFKI")]
    [InlineData(" anatr ", "ANATR")]
    public void Customer_code_is_normalized(string code, string expected)
    {
        Assert.Equal(expected, Customer.NormalizeCode(code).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("TOOLONG")]
    [InlineData("A-B")]
    public void Invalid_customer_code_is_rejected(string code)
    {
        Assert.True(Customer.NormalizeCode(code).IsFailure);
    }

    [Fact]
    public void Address_requires_street_city_and_country_and_compares_by_value()
    {
        Result<Address> invalid = Address.Create("", "", null, null, "");
        Assert.Equal(3, invalid.Errors.Length);

        Assert.Equal(Address.Create("A", "B", null, "1", "C").Value, Address.Create("A ", "B", "", "1", "C").Value);
    }
}
