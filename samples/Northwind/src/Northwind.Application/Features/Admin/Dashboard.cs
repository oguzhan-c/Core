using Can.Core.Application;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Features.Orders;
using Northwind.Domain.Catalog;
using Northwind.Domain.Customers;
using Northwind.Domain.Orders;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Admin;

public sealed record DashboardDto(
    int ProductCount,
    int CustomerCount,
    int OpenOrderCount,
    int ProductsToReorder,
    decimal TotalRevenue,
    IReadOnlyList<OrderListItemDto> RecentOrders);

/// <summary>Yönetim paneli özeti.</summary>
public sealed record GetDashboardQuery : IRequest<DashboardDto>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

public sealed class GetDashboardQueryHandler : IRequestHandler<GetDashboardQuery, DashboardDto>
{
    private readonly IRepository<Product, Guid> _products;
    private readonly IRepository<Customer, Guid> _customers;
    private readonly IRepository<Order, Guid> _orders;

    public GetDashboardQueryHandler(IRepository<Product, Guid> products, IRepository<Customer, Guid> customers, IRepository<Order, Guid> orders)
    {
        _products = products;
        _customers = customers;
        _orders = orders;
    }

    public async Task<DashboardDto> Handle(GetDashboardQuery request, CancellationToken cancellationToken)
    {
        // Aynı DbContext üzerinde sorgular sırayla çalışmalı (paralel değil).
        int productCount = await _products.CountAsync(p => !p.IsDiscontinued, cancellationToken: cancellationToken);
        int customerCount = await _customers.CountAsync(cancellationToken: cancellationToken);
        int openOrderCount = await _orders.CountAsync(o => o.Status == OrderStatus.Placed, cancellationToken: cancellationToken);
        int toReorder = await _products.CountAsync(
            p => !p.IsDiscontinued && p.UnitsInStock + p.UnitsOnOrder <= p.ReorderLevel,
            cancellationToken: cancellationToken
        );

        decimal revenue = await _orders
            .Query(enableTracking: false)
            .Where(o => o.Status != OrderStatus.Cancelled)
            .SelectMany(o => o.Lines)
            .SumAsync(l => l.UnitPrice * l.Quantity * (1 - l.Discount), cancellationToken);

        IQueryable<Customer> customers = _customers.Query(withDeleted: true, enableTracking: false);
        List<OrderListItemDto> recent = await _orders
            .Query(enableTracking: false)
            .OrderByDescending(o => o.Number)
            .Take(8)
            .Select(o => new OrderListItemDto(
                o.Id,
                o.Number,
                o.CustomerId,
                customers.Where(c => c.Id == o.CustomerId).Select(c => c.CompanyName).FirstOrDefault() ?? "",
                o.Status,
                o.OrderedAt,
                o.ShippedAt,
                o.Lines.Sum(l => l.UnitPrice * l.Quantity * (1 - l.Discount)) + o.Freight
            ))
            .ToListAsync(cancellationToken);

        return new DashboardDto(productCount, customerCount, openOrderCount, toReorder, Math.Round(revenue, 2), recent);
    }
}
