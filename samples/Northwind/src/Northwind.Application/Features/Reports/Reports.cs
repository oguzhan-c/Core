using Can.Core.Application;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Features.Orders;
using Northwind.Domain.Catalog;
using Northwind.Domain.Customers;
using Northwind.Domain.Orders;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Reports;

public sealed record SalesByCategoryDto(string Category, int Orders, int Quantity, decimal Revenue);

public sealed record TopCustomerDto(Guid CustomerId, string CompanyName, string? Country, int Orders, decimal Revenue);

/// <summary>Kategorilere göre satış (iptal edilenler hariç). 5 dakika önbelleğe alınır.</summary>
public sealed record GetSalesByCategoryQuery(DateOnly? From = null, DateOnly? To = null)
    : IRequest<IReadOnlyList<SalesByCategoryDto>>, ISecuredRequest, ICachableRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Sales];
    public string CacheKey => $"reports:sales-by-category:{From}:{To}";
    public IReadOnlyCollection<string> CacheTags => [OrderCacheTags.Reports];
    public TimeSpan? CacheExpiration => TimeSpan.FromMinutes(5);
}

/// <summary>En çok ciro yapılan müşteriler (kargo ücreti hariç).</summary>
public sealed record GetTopCustomersQuery(int Count = 10, DateOnly? From = null, DateOnly? To = null)
    : IRequest<IReadOnlyList<TopCustomerDto>>, ISecuredRequest, ICachableRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Sales];
    public string CacheKey => $"reports:top-customers:{Count}:{From}:{To}";
    public IReadOnlyCollection<string> CacheTags => [OrderCacheTags.Reports];
    public TimeSpan? CacheExpiration => TimeSpan.FromMinutes(5);
}

public sealed class GetTopCustomersQueryValidator : AbstractValidator<GetTopCustomersQuery>
{
    public GetTopCustomersQueryValidator() => RuleFor(q => q.Count).InclusiveBetween(1, 100);
}

public sealed class ReportQueryHandlers
    : IRequestHandler<GetSalesByCategoryQuery, IReadOnlyList<SalesByCategoryDto>>,
        IRequestHandler<GetTopCustomersQuery, IReadOnlyList<TopCustomerDto>>
{
    private const string Uncategorized = "(Kategorisiz)";

    private readonly IRepository<Order, Guid> _orders;
    private readonly IRepository<Product, Guid> _products;
    private readonly IRepository<Category, Guid> _categories;
    private readonly IRepository<Customer, Guid> _customers;

    public ReportQueryHandlers(
        IRepository<Order, Guid> orders,
        IRepository<Product, Guid> products,
        IRepository<Category, Guid> categories,
        IRepository<Customer, Guid> customers)
    {
        _orders = orders;
        _products = products;
        _categories = categories;
        _customers = customers;
    }

    public async Task<IReadOnlyList<SalesByCategoryDto>> Handle(GetSalesByCategoryQuery request, CancellationToken cancellationToken)
    {
        var lines =
            from o in CompletedOrders(request.From, request.To)
            from l in o.Lines
            join p in _products.Query(withDeleted: true, enableTracking: false) on l.ProductId equals p.Id
            join c in _categories.Query(withDeleted: true, enableTracking: false) on p.CategoryId equals (Guid?)c.Id into categoryGroup
            from c in categoryGroup.DefaultIfEmpty()
            select new
            {
                OrderId = o.Id,
                Category = c == null ? Uncategorized : c.Name,
                l.Quantity,
                Amount = l.UnitPrice * l.Quantity * (1 - l.Discount),
            };

        var rows = await lines
            .GroupBy(x => x.Category)
            .Select(g => new
            {
                Category = g.Key,
                Orders = g.Select(x => x.OrderId).Distinct().Count(),
                Quantity = g.Sum(x => x.Quantity),
                Revenue = g.Sum(x => x.Amount),
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new SalesByCategoryDto(r.Category, r.Orders, r.Quantity, Math.Round(r.Revenue, 2)))
            .OrderByDescending(r => r.Revenue)
            .ToList();
    }

    public async Task<IReadOnlyList<TopCustomerDto>> Handle(GetTopCustomersQuery request, CancellationToken cancellationToken)
    {
        var totals = await (
                from o in CompletedOrders(request.From, request.To)
                from l in o.Lines
                select new { OrderId = o.Id, o.CustomerId, Amount = l.UnitPrice * l.Quantity * (1 - l.Discount) }
            )
            .GroupBy(x => x.CustomerId)
            .Select(g => new
            {
                CustomerId = g.Key,
                Orders = g.Select(x => x.OrderId).Distinct().Count(),
                Revenue = g.Sum(x => x.Amount),
            })
            .OrderByDescending(x => x.Revenue)
            .Take(request.Count)
            .ToListAsync(cancellationToken);

        Guid[] ids = totals.Select(t => t.CustomerId).ToArray();
        var customers = await _customers
            .Query(withDeleted: true, enableTracking: false)
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.CompanyName, Country = c.Address == null ? null : c.Address.Country })
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        return totals
            .Select(t =>
            {
                var customer = customers.GetValueOrDefault(t.CustomerId);
                return new TopCustomerDto(t.CustomerId, customer?.CompanyName ?? "?", customer?.Country, t.Orders, Math.Round(t.Revenue, 2));
            })
            .ToList();
    }

    private IQueryable<Order> CompletedOrders(DateOnly? from, DateOnly? to)
    {
        IQueryable<Order> query = _orders.Query(enableTracking: false).Where(o => o.Status != OrderStatus.Cancelled);

        if (from is { } f)
        {
            DateTimeOffset start = ReportPeriod.StartOf(f);
            query = query.Where(o => o.OrderedAt >= start);
        }

        if (to is { } t)
        {
            DateTimeOffset end = ReportPeriod.StartOf(t.AddDays(1));
            query = query.Where(o => o.OrderedAt < end);
        }

        return query;
    }
}
