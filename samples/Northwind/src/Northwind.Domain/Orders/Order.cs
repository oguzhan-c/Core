using Can.Core.Domain.Auditing;
using Can.Core.Domain.Entities;
using Can.Core.Domain.Exceptions;
using Northwind.Domain.Catalog;
using Northwind.Domain.Common;

namespace Northwind.Domain.Orders;

public enum OrderStatus
{
    Placed = 1,
    Shipped = 2,
    Cancelled = 3,
}

/// <summary>
/// Sipariş aggregate'i. Satırlar yalnızca sipariş üzerinden değiştirilir; satır eklemek ürün stoğundan düşer.
/// </summary>
[Audited]
public sealed class Order : TenantAggregateRoot
{
    public const int ShipNameMaxLength = 60;

    private readonly List<OrderLine> _lines = [];

    private Order()
    {
        ShipName = string.Empty;
        ShipAddress = null!;
    }

    private Order(Guid id)
        : base(id)
    {
        ShipName = string.Empty;
        ShipAddress = null!;
    }

    /// <summary>Mağaza içinde benzersiz, insan okunur sipariş numarası (ör. 10248).</summary>
    public int Number { get; private set; }

    public Guid CustomerId { get; private set; }

    /// <summary>Siparişi alan çalışan.</summary>
    public Guid? EmployeeId { get; private set; }

    public OrderStatus Status { get; private set; }

    public DateTimeOffset OrderedAt { get; private set; }

    public DateOnly? RequiredDate { get; private set; }

    public DateTimeOffset? ShippedAt { get; private set; }

    public Guid? ShipperId { get; private set; }

    public decimal Freight { get; private set; }

    public string ShipName { get; private set; }

    public Address ShipAddress { get; private set; }

    public IReadOnlyCollection<OrderLine> Lines => _lines;

    public decimal Subtotal => _lines.Sum(l => l.LineTotal);

    public decimal Total => Subtotal + Freight;

    public static Order Place(
        int number,
        Guid customerId,
        Guid? employeeId,
        string shipName,
        Address shipAddress,
        DateOnly? requiredDate,
        decimal freight,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(shipAddress);

        var order = new Order(Guid.CreateVersion7())
        {
            Number = Check.Positive(number, "Sipariş numarası"),
            CustomerId = customerId,
            EmployeeId = employeeId,
            Status = OrderStatus.Placed,
            OrderedAt = now,
            RequiredDate = requiredDate,
            Freight = Check.NotNegative(freight, "Kargo ücreti"),
            ShipName = Check.Required(shipName, "Alıcı", ShipNameMaxLength),
            ShipAddress = shipAddress,
        };

        order.RaiseDomainEvent(new OrderPlaced(order.Id, number, customerId));
        return order;
    }

    /// <summary>Ürünü siparişe ekler ve stoğundan düşer. Aynı ürün tekrar eklenirse miktar artar.</summary>
    /// <param name="product">Eklenen ürün; fiyatı o anki birim fiyattır.</param>
    /// <param name="quantity">Miktar.</param>
    /// <param name="discount">İndirim oranı, 0 ile 1 arası (ör. 0,15 = %15).</param>
    public void AddLine(Product product, int quantity, decimal discount = 0)
    {
        ArgumentNullException.ThrowIfNull(product);
        EnsureStatus(OrderStatus.Placed, "Yalnızca yeni siparişlere ürün eklenebilir.");

        if (discount is < 0 or > 1)
            throw new BusinessException("İndirim 0 ile 1 arasında olmalı.");

        product.ReserveStock(quantity);

        OrderLine? existing = _lines.FirstOrDefault(l => l.ProductId == product.Id);
        if (existing is not null)
            existing.Increase(quantity);
        else
            _lines.Add(new OrderLine(Id, product.Id, product.Name, product.UnitPrice, quantity, discount));
    }

    public void Ship(Guid shipperId, DateTimeOffset now)
    {
        EnsureStatus(OrderStatus.Placed, "Yalnızca yeni siparişler kargoya verilebilir.");

        if (_lines.Count == 0)
            throw new BusinessException("Ürünü olmayan sipariş kargoya verilemez.");

        Status = OrderStatus.Shipped;
        ShipperId = shipperId;
        ShippedAt = now;
        RaiseDomainEvent(new OrderShipped(Id, Number, CustomerId, shipperId, Total));
    }

    public void Cancel()
    {
        if (Status == OrderStatus.Cancelled)
            return;

        EnsureStatus(OrderStatus.Placed, "Kargoya verilmiş sipariş iptal edilemez.");

        Status = OrderStatus.Cancelled;
        RaiseDomainEvent(new OrderCancelled(Id, Number, _lines.Select(l => new OrderedQuantity(l.ProductId, l.Quantity)).ToList()));
    }

    /// <summary>Hazır veriyi (Northwind) içe aktarırken: stok düşmez, event üretmez.</summary>
    public static Order Import(
        int number,
        Guid customerId,
        Guid? employeeId,
        DateTimeOffset orderedAt,
        DateOnly? requiredDate,
        DateTimeOffset? shippedAt,
        Guid? shipperId,
        decimal freight,
        string shipName,
        Address shipAddress,
        IEnumerable<(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity, decimal Discount)> lines)
    {
        var order = new Order(Guid.CreateVersion7())
        {
            Number = number,
            CustomerId = customerId,
            EmployeeId = employeeId,
            Status = shippedAt is null ? OrderStatus.Placed : OrderStatus.Shipped,
            OrderedAt = orderedAt,
            RequiredDate = requiredDate,
            ShippedAt = shippedAt,
            ShipperId = shipperId,
            Freight = freight,
            ShipName = shipName,
            ShipAddress = shipAddress,
        };

        foreach (var line in lines)
            order._lines.Add(new OrderLine(order.Id, line.ProductId, line.ProductName, line.UnitPrice, line.Quantity, line.Discount));

        return order;
    }

    private void EnsureStatus(OrderStatus expected, string message)
    {
        if (Status != expected)
            throw new BusinessException(message);
    }
}

/// <summary>Sipariş satırı. Ürün adı ve fiyatı sipariş anındaki hâliyle saklanır.</summary>
public sealed class OrderLine : Entity<Guid>
{
    private OrderLine()
    {
        ProductName = string.Empty;
    }

    internal OrderLine(Guid orderId, Guid productId, string productName, decimal unitPrice, int quantity, decimal discount)
        : base(Guid.CreateVersion7())
    {
        OrderId = orderId;
        ProductId = productId;
        ProductName = productName;
        UnitPrice = unitPrice;
        Quantity = Check.Positive(quantity, "Miktar");
        Discount = discount;
    }

    public Guid OrderId { get; private set; }

    public Guid ProductId { get; private set; }

    public string ProductName { get; private set; }

    public decimal UnitPrice { get; private set; }

    public int Quantity { get; private set; }

    public decimal Discount { get; private set; }

    public decimal LineTotal => Math.Round(UnitPrice * Quantity * (1 - Discount), 2, MidpointRounding.AwayFromZero);

    internal void Increase(int quantity) => Quantity += Check.Positive(quantity, "Miktar");
}
