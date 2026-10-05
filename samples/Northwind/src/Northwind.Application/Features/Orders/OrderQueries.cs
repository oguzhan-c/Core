using Can.Core.Application;
using Can.Core.Application.Exceptions;
using Can.Core.Mapping;
using Can.Core.Mediator;
using Can.Core.Persistence.Dynamic;
using Can.Core.Persistence.Paging;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Common;
using Northwind.Domain.Customers;
using Northwind.Domain.Employees;
using Northwind.Domain.Orders;
using Northwind.Domain.Shipping;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Orders;

public sealed record OrderListItemDto(
    Guid Id,
    int Number,
    Guid CustomerId,
    string CustomerName,
    OrderStatus Status,
    DateTimeOffset OrderedAt,
    DateTimeOffset? ShippedAt,
    decimal Total);

public sealed record OrderLineDto(Guid ProductId, string ProductName, decimal UnitPrice, int Quantity, decimal Discount, decimal LineTotal);

public sealed record OrderDto(
    Guid Id,
    int Number,
    OrderStatus Status,
    Guid CustomerId,
    string CustomerName,
    Guid? EmployeeId,
    string? EmployeeName,
    DateTimeOffset OrderedAt,
    DateOnly? RequiredDate,
    DateTimeOffset? ShippedAt,
    Guid? ShipperId,
    string? ShipperName,
    string ShipName,
    AddressDto ShipAddress,
    decimal Freight,
    decimal Subtotal,
    decimal Total,
    IReadOnlyList<OrderLineDto> Lines);

public sealed class OrderProfile : MappingProfile
{
    public OrderProfile()
    {
        CreateMap<OrderLine, OrderLineDto>();
    }
}

public sealed record GetOrderListQuery(
    PageRequest Page,
    Guid? CustomerId = null,
    OrderStatus? Status = null,
    DateOnly? From = null,
    DateOnly? To = null) : IRequest<IPaginate<OrderListItemDto>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

public sealed record GetOrderByIdQuery(Guid Id) : IRequest<OrderDto>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

/// <summary>
/// Dinamik filtre/sıralama ile sipariş arama, ör. <c>freight gt 100</c>, <c>shipAddress.country in Germany,France</c>,
/// <c>status eq Shipped</c>, <c>orderedAt between 1997-01-01,1997-12-31</c>.
/// </summary>
public sealed record SearchOrdersQuery(DynamicQuery Query, PageRequest Page) : IRequest<IPaginate<OrderListItemDto>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

public sealed class SearchOrdersQueryValidator : AbstractValidator<SearchOrdersQuery>
{
    public SearchOrdersQueryValidator()
    {
        RuleFor(q => q.Query).NotNull();
        RuleFor(q => q.Page).ValidPage();
    }
}

public sealed class GetOrderListQueryValidator : AbstractValidator<GetOrderListQuery>
{
    public GetOrderListQueryValidator()
    {
        RuleFor(q => q.Page).ValidPage();
        RuleFor(q => q.To).GreaterThanOrEqualTo(q => q.From).When(q => q.From is not null && q.To is not null);
    }
}

public sealed class OrderQueryHandlers
    : IRequestHandler<GetOrderListQuery, IPaginate<OrderListItemDto>>,
        IRequestHandler<GetOrderByIdQuery, OrderDto>,
        IRequestHandler<SearchOrdersQuery, IPaginate<OrderListItemDto>>
{
    private readonly IRepository<Order, Guid> _orders;
    private readonly IRepository<Customer, Guid> _customers;
    private readonly OrderDetails _details;

    public OrderQueryHandlers(IRepository<Order, Guid> orders, IRepository<Customer, Guid> customers, OrderDetails details)
    {
        _orders = orders;
        _customers = customers;
        _details = details;
    }

    public Task<IPaginate<OrderListItemDto>> Handle(GetOrderListQuery request, CancellationToken cancellationToken)
    {
        IQueryable<Order> query = _orders.Query(enableTracking: false);

        if (request.CustomerId is { } customerId)
            query = query.Where(o => o.CustomerId == customerId);

        if (request.Status is { } status)
            query = query.Where(o => o.Status == status);

        if (request.From is { } from)
        {
            DateTimeOffset fromTime = ReportPeriod.StartOf(from);
            query = query.Where(o => o.OrderedAt >= fromTime);
        }

        if (request.To is { } to)
        {
            DateTimeOffset toTime = ReportPeriod.StartOf(to.AddDays(1));
            query = query.Where(o => o.OrderedAt < toTime);
        }

        return ToListItems(query.OrderByDescending(o => o.Number))
            .ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken);
    }

    public Task<IPaginate<OrderListItemDto>> Handle(SearchOrdersQuery request, CancellationToken cancellationToken) =>
        ToListItems(DynamicSearch.Apply(_orders.Query(enableTracking: false), request.Query, q => q.OrderByDescending(o => o.Number)))
            .ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken);

    /// <summary>Sıralanmış sipariş sorgusunu müşteri adı ve toplamla liste öğesine çevirir (tek SQL sorgusu).</summary>
    private IQueryable<OrderListItemDto> ToListItems(IQueryable<Order> orders)
    {
        IQueryable<Customer> customers = _customers.Query(withDeleted: true, enableTracking: false);

        return orders.Select(o => new OrderListItemDto(
            o.Id,
            o.Number,
            o.CustomerId,
            customers.Where(c => c.Id == o.CustomerId).Select(c => c.CompanyName).FirstOrDefault() ?? "",
            o.Status,
            o.OrderedAt,
            o.ShippedAt,
            o.Lines.Sum(l => l.UnitPrice * l.Quantity * (1 - l.Discount)) + o.Freight
        ));
    }

    public Task<OrderDto> Handle(GetOrderByIdQuery request, CancellationToken cancellationToken) =>
        _details.GetAsync(request.Id, customerId: null, cancellationToken);
}

/// <summary>Sipariş ayrıntısı: satırlar ile müşteri, çalışan ve kargo firması adları.</summary>
public sealed class OrderDetails
{
    private readonly IRepository<Order, Guid> _orders;
    private readonly IRepository<Customer, Guid> _customers;
    private readonly IRepository<Employee, Guid> _employees;
    private readonly IRepository<Shipper, Guid> _shippers;
    private readonly IMapper _mapper;

    public OrderDetails(
        IRepository<Order, Guid> orders,
        IRepository<Customer, Guid> customers,
        IRepository<Employee, Guid> employees,
        IRepository<Shipper, Guid> shippers,
        IMapper mapper)
    {
        _orders = orders;
        _customers = customers;
        _employees = employees;
        _shippers = shippers;
        _mapper = mapper;
    }

    /// <param name="id">Sipariş.</param>
    /// <param name="customerId">Doluysa sipariş bu müşteriye ait olmalı (müşteri yalnızca kendi siparişini görür).</param>
    /// <param name="cancellationToken">İptal belirteci.</param>
    public async Task<OrderDto> GetAsync(Guid id, Guid? customerId, CancellationToken cancellationToken)
    {
        Order? order = await _orders.GetByIdAsync(id, include: q => q.Include(o => o.Lines), enableTracking: false, cancellationToken: cancellationToken);
        if (order is null || (customerId is { } c && order.CustomerId != c))
            throw NotFoundException.For<Order>(id);

        string customerName = await _customers
            .Query(withDeleted: true, enableTracking: false)
            .Where(x => x.Id == order.CustomerId)
            .Select(x => x.CompanyName)
            .FirstOrDefaultAsync(cancellationToken) ?? "";

        string? employeeName = order.EmployeeId is { } employeeId
            ? await _employees
                .Query(withDeleted: true, enableTracking: false)
                .Where(e => e.Id == employeeId)
                .Select(e => e.FirstName + " " + e.LastName)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        string? shipperName = order.ShipperId is { } shipperId
            ? await _shippers
                .Query(withDeleted: true, enableTracking: false)
                .Where(s => s.Id == shipperId)
                .Select(s => s.CompanyName)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        return new OrderDto(
            order.Id,
            order.Number,
            order.Status,
            order.CustomerId,
            customerName,
            order.EmployeeId,
            employeeName,
            order.OrderedAt,
            order.RequiredDate,
            order.ShippedAt,
            order.ShipperId,
            shipperName,
            order.ShipName,
            _mapper.Map<AddressDto>(order.ShipAddress)!,
            order.Freight,
            order.Subtotal,
            order.Total,
            order.Lines.Select(l => _mapper.Map<OrderLine, OrderLineDto>(l)!).ToList()
        );
    }
}

/// <summary>Tarih filtrelerini UTC zaman damgasına çevirir (gün başı).</summary>
public static class ReportPeriod
{
    public static DateTimeOffset StartOf(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}
