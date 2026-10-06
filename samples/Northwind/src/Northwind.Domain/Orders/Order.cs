using Can.Core.Domain.Auditing;
using Can.Core.Domain.Entities;
using Can.Core.Domain.Results;
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

    public static Result<Order> Place(
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

        Result<Success> valid = Result.Validate(
            Check.Positive(number, "Sipariş numarası"),
            Check.NotNegative(freight, "Kargo ücreti"),
            Check.Required(shipName, "Alıcı", ShipNameMaxLength)
        );
        if (valid.IsFailure)
            return valid.Errors;

        var order = new Order(Guid.CreateVersion7())
        {
            Number = number,
            CustomerId = customerId,
            EmployeeId = employeeId,
            Status = OrderStatus.Placed,
            OrderedAt = now,
            RequiredDate = requiredDate,
            Freight = freight,
            ShipName = Check.Clean(shipName),
            ShipAddress = shipAddress,
        };

        order.RaiseDomainEvent(new OrderPlaced(order.Id, number, customerId));
        return order;
    }

    /// <summary>Ürün ekler ve stoğu ayırır. Aynı ürün tekrar eklenirse miktarı artar.</summary>
    public Result<Success> AddLine(Product product, int quantity, decimal discount = 0)
    {
        ArgumentNullException.ThrowIfNull(product);

        if (Status != OrderStatus.Placed)
            return OrderErrors.NotEditable;

        if (discount is < 0 or > 1)
            return OrderErrors.InvalidDiscount;

        if (Check.Positive(quantity, "Miktar") is { } error)
            return error;

        Result<Success> reserved = product.ReserveStock(quantity);
        if (reserved.IsFailure)
            return reserved;

        OrderLine? existing = _lines.FirstOrDefault(l => l.ProductId == product.Id);
        if (existing is not null)
            existing.Increase(quantity);
        else
            _lines.Add(new OrderLine(Id, product.Id, product.Name, product.UnitPrice, quantity, discount));

        return Result.Success;
    }

    public Result<Success> Ship(Guid shipperId, DateTimeOffset now)
    {
        if (Status != OrderStatus.Placed)
            return OrderErrors.NotShippable;

        if (_lines.Count == 0)
            return OrderErrors.Empty;

        Status = OrderStatus.Shipped;
        ShipperId = shipperId;
        ShippedAt = now;
        RaiseDomainEvent(new OrderShipped(Id, Number, CustomerId, shipperId, Total));
        return Result.Success;
    }

    /// <summary>İptal eder; ayrılan stok <see cref="OrderCancelled"/> event'i ile geri döner. Zaten iptalse bir şey yapmaz.</summary>
    public Result<Success> Cancel()
    {
        if (Status == OrderStatus.Cancelled)
            return Result.Success;

        if (Status != OrderStatus.Placed)
            return OrderErrors.AlreadyShipped;

        Status = OrderStatus.Cancelled;
        RaiseDomainEvent(new OrderCancelled(Id, Number, _lines.Select(l => new OrderedQuantity(l.ProductId, l.Quantity)).ToList()));
        return Result.Success;
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
        Quantity = quantity;
        Discount = discount;
    }

    public Guid OrderId { get; private set; }

    public Guid ProductId { get; private set; }

    public string ProductName { get; private set; }

    public decimal UnitPrice { get; private set; }

    public int Quantity { get; private set; }

    public decimal Discount { get; private set; }

    public decimal LineTotal => Math.Round(UnitPrice * Quantity * (1 - Discount), 2, MidpointRounding.AwayFromZero);

    internal void Increase(int quantity) => Quantity += quantity;
}

/// <summary>Sipariş hataları.</summary>
public static class OrderErrors
{
    public static readonly Error NotEditable = Error.Failure("order.not_editable", "Yalnızca yeni siparişlere ürün eklenebilir.");
    public static readonly Error NotShippable = Error.Failure("order.not_shippable", "Yalnızca yeni siparişler kargoya verilebilir.");
    public static readonly Error AlreadyShipped = Error.Failure("order.already_shipped", "Kargoya verilmiş sipariş iptal edilemez.");
    public static readonly Error Empty = Error.Failure("order.empty", "Ürünü olmayan sipariş kargoya verilemez.");
    public static readonly Error InvalidDiscount = Error.Validation("order.invalid_discount", "İndirim 0 ile 1 arasında olmalı.", "Discount");

    public static Error NotFound(Guid id) => Error.NotFound("order.not_found", $"'{id}' siparişi bulunamadı.");
}
