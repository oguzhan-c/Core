using Can.Core.Application;
using Can.Core.Application.Exceptions;
using Can.Core.Domain.Exceptions;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Common;
using Northwind.Domain.Catalog;
using Northwind.Domain.Common;
using Northwind.Domain.Customers;
using Northwind.Domain.Employees;
using Northwind.Domain.Orders;
using Northwind.Domain.Shipping;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Orders;

public sealed record PlaceOrderLine(Guid ProductId, int Quantity, decimal Discount = 0);

/// <summary>Teslimat bilgisi verilmezse müşterinin adı ve adresi kullanılır.</summary>
public sealed record ShipToDto(string Name, AddressDto Address);

public sealed record PlaceOrderResult(Guid Id, int Number, decimal Total);

/// <summary>
/// Yeni sipariş: ürünlerin stoğu düşülür, sipariş numarası verilir. Hepsi tek transaction'da; bir ürünün stoğu
/// yetmezse hiçbir şey kaydedilmez.
/// </summary>
public sealed record PlaceOrderCommand(
    Guid CustomerId,
    Guid? EmployeeId,
    DateOnly? RequiredDate,
    decimal Freight,
    ShipToDto? ShipTo,
    IReadOnlyList<PlaceOrderLine> Lines) : IRequest<PlaceOrderResult>, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Sales];
    public IReadOnlyCollection<string> CacheTagsToRemove => [OrderCacheTags.Reports];
}

public sealed record ShipOrderCommand(Guid OrderId, Guid ShipperId) : IRequest, ISecuredRequest, ITransactionalRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Warehouse];
}

/// <summary>Siparişi iptal eder; ürünler <see cref="OrderCancelled"/> handler'ı ile aynı transaction'da stoğa geri konur.</summary>
public sealed record CancelOrderCommand(Guid OrderId) : IRequest, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Sales];
    public IReadOnlyCollection<string> CacheTagsToRemove => [OrderCacheTags.Reports];
}

public static class OrderCacheTags
{
    /// <summary>Satış raporları; sipariş eklenince/iptal edilince temizlenir.</summary>
    public const string Reports = "reports";
}

public sealed class PlaceOrderCommandValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderCommandValidator()
    {
        RuleFor(c => c.CustomerId).NotEmpty();
        RuleFor(c => c.Freight).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, ignoreTrailingZeros: true);
        RuleFor(c => c.Lines).NotEmpty().Must(l => l.Count <= 100).WithMessage("Bir siparişte en fazla 100 satır olabilir.");
        RuleForEach(c => c.Lines).ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, 10_000);
            line.RuleFor(l => l.Discount).InclusiveBetween(0, 1);
        });
        RuleFor(c => c.ShipTo!.Name).NotEmpty().MaximumLength(Order.ShipNameMaxLength).When(c => c.ShipTo is not null);
        RuleFor(c => c.ShipTo)
            .Must(s => ValidationExtensions.IsComplete(s!.Address))
            .WithMessage(ValidationExtensions.AddressMessage)
            .When(c => c.ShipTo is not null);
    }
}

public sealed class ShipOrderCommandValidator : AbstractValidator<ShipOrderCommand>
{
    public ShipOrderCommandValidator()
    {
        RuleFor(c => c.OrderId).NotEmpty();
        RuleFor(c => c.ShipperId).NotEmpty();
    }
}

public sealed class OrderCommandHandlers
    : IRequestHandler<PlaceOrderCommand, PlaceOrderResult>,
        IRequestHandler<ShipOrderCommand>,
        IRequestHandler<CancelOrderCommand>
{
    private readonly IRepository<Order, Guid> _orders;
    private readonly IRepository<Customer, Guid> _customers;
    private readonly IRepository<Employee, Guid> _employees;
    private readonly IRepository<Shipper, Guid> _shippers;
    private readonly OrderPlacer _placer;
    private readonly TimeProvider _timeProvider;

    public OrderCommandHandlers(
        IRepository<Order, Guid> orders,
        IRepository<Customer, Guid> customers,
        IRepository<Employee, Guid> employees,
        IRepository<Shipper, Guid> shippers,
        OrderPlacer placer,
        TimeProvider timeProvider)
    {
        _orders = orders;
        _customers = customers;
        _employees = employees;
        _shippers = shippers;
        _placer = placer;
        _timeProvider = timeProvider;
    }

    public async Task<PlaceOrderResult> Handle(PlaceOrderCommand request, CancellationToken cancellationToken)
    {
        Customer customer =
            await _customers.GetByIdAsync(request.CustomerId, enableTracking: false, cancellationToken: cancellationToken)
            ?? throw NotFoundException.For<Customer>(request.CustomerId);

        if (request.EmployeeId is { } employeeId && !await _employees.AnyAsync(e => e.Id == employeeId, cancellationToken: cancellationToken))
            throw NotFoundException.For<Employee>(employeeId);

        Order order = await _placer.PlaceAsync(customer, request.EmployeeId, request.RequiredDate, _ => request.Freight, request.ShipTo, request.Lines, cancellationToken);
        return new PlaceOrderResult(order.Id, order.Number, order.Total);
    }

    public async Task Handle(ShipOrderCommand request, CancellationToken cancellationToken)
    {
        if (!await _shippers.AnyAsync(s => s.Id == request.ShipperId, cancellationToken: cancellationToken))
            throw NotFoundException.For<Shipper>(request.ShipperId);

        Order order = await LoadAsync(request.OrderId, cancellationToken);
        order.Ship(request.ShipperId, _timeProvider.GetUtcNow());
    }

    public async Task Handle(CancelOrderCommand request, CancellationToken cancellationToken)
    {
        Order order = await LoadAsync(request.OrderId, cancellationToken);
        order.Cancel();
    }

    private async Task<Order> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        await _orders.GetByIdAsync(id, include: q => q.Include(o => o.Lines), cancellationToken: cancellationToken)
        ?? throw NotFoundException.For<Order>(id);
}

/// <summary>
/// Sipariş oluşturma: ürünleri yükler, numara verir, satırları ekler (stok düşer). Personelin ve müşterinin
/// (site) sipariş akışları bunu ortak kullanır; transaction'ı çağıran command sağlar.
/// </summary>
public sealed class OrderPlacer
{
    private const int FirstOrderNumber = 10000;

    private readonly IRepository<Order, Guid> _orders;
    private readonly IRepository<Product, Guid> _products;
    private readonly TimeProvider _timeProvider;

    public OrderPlacer(IRepository<Order, Guid> orders, IRepository<Product, Guid> products, TimeProvider timeProvider)
    {
        _orders = orders;
        _products = products;
        _timeProvider = timeProvider;
    }

    /// <param name="customer">Siparişi veren müşteri.</param>
    /// <param name="employeeId">Siparişi alan çalışan (site siparişlerinde boş).</param>
    /// <param name="requiredDate">İstenen teslim tarihi.</param>
    /// <param name="freight">Ürünlerin ara toplamından kargo ücretini hesaplar.</param>
    /// <param name="shipTo">Teslimat bilgisi; boşsa müşterinin adı ve adresi.</param>
    /// <param name="lines">Satırlar.</param>
    /// <param name="cancellationToken">İptal belirteci.</param>
    public async Task<Order> PlaceAsync(
        Customer customer,
        Guid? employeeId,
        DateOnly? requiredDate,
        Func<decimal, decimal> freight,
        ShipToDto? shipTo,
        IReadOnlyList<PlaceOrderLine> lines,
        CancellationToken cancellationToken)
    {
        (string shipName, Address shipAddress) = shipTo is not null
            ? (shipTo.Name, shipTo.Address.ToAddress())
            : (customer.CompanyName, customer.Address ?? throw new BusinessException("Teslimat adresi gerekli.") { Code = "address_required" });

        Guid[] productIds = lines.Select(l => l.ProductId).Distinct().ToArray();
        Dictionary<Guid, Product> products = await _products
            .Query()
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        Guid? missing = productIds.Where(id => !products.ContainsKey(id)).Select(id => (Guid?)id).FirstOrDefault();
        if (missing is { } missingId)
            throw NotFoundException.For<Product>(missingId);

        decimal subtotal = lines.Sum(l => products[l.ProductId].UnitPrice * l.Quantity * (1 - l.Discount));

        // Numara çakışması (aynı anda iki sipariş) benzersiz index ile engellenir ve 409 döner.
        int lastNumber = await _orders.Query(withDeleted: true, enableTracking: false).MaxAsync(o => (int?)o.Number, cancellationToken) ?? FirstOrderNumber;

        Order order = Order.Place(
            lastNumber + 1,
            customer.Id,
            employeeId,
            shipName,
            shipAddress,
            requiredDate,
            freight(subtotal),
            _timeProvider.GetUtcNow()
        );

        foreach (PlaceOrderLine line in lines)
            order.AddLine(products[line.ProductId], line.Quantity, line.Discount);

        await _orders.AddAsync(order, cancellationToken);
        return order;
    }
}
