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
using Northwind.Domain.Catalog;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Products;

/// <summary>Ürün listesi: kategori ve arama filtresi, kategori/tedarikçi adlarıyla.</summary>
public sealed record GetProductListQuery(PageRequest Page, Guid? CategoryId = null, string? Search = null, bool IncludeDiscontinued = false)
    : IRequest<IPaginate<ProductListItemDto>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

/// <summary>
/// İstemcinin gönderdiği dinamik filtre ve sıralama ile arama, ör.
/// <c>{"filter":{"field":"unitPrice","operator":"gt","value":"20"},"sort":[{"field":"name","dir":"asc"}]}</c>.
/// </summary>
public sealed record SearchProductsQuery(DynamicQuery Query, PageRequest Page) : IRequest<IPaginate<ProductDto>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

public sealed record GetProductByIdQuery(Guid Id) : IRequest<ProductDto>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

/// <summary>Yeniden sipariş verilmesi gereken ürünler.</summary>
public sealed record GetProductsToReorderQuery : IRequest<IReadOnlyList<ProductDto>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

public sealed class GetProductListQueryValidator : AbstractValidator<GetProductListQuery>
{
    public GetProductListQueryValidator()
    {
        RuleFor(q => q.Page).ValidPage();
        RuleFor(q => q.Search).MaximumLength(100);
    }
}

public sealed class SearchProductsQueryValidator : AbstractValidator<SearchProductsQuery>
{
    public SearchProductsQueryValidator()
    {
        RuleFor(q => q.Query).NotNull();
        RuleFor(q => q.Page).ValidPage();
    }
}

public sealed class ProductQueryHandlers
    : IRequestHandler<GetProductListQuery, IPaginate<ProductListItemDto>>,
        IRequestHandler<SearchProductsQuery, IPaginate<ProductDto>>,
        IRequestHandler<GetProductByIdQuery, ProductDto>,
        IRequestHandler<GetProductsToReorderQuery, IReadOnlyList<ProductDto>>
{
    private readonly IRepository<Product, Guid> _products;
    private readonly IRepository<Category, Guid> _categories;
    private readonly IRepository<Supplier, Guid> _suppliers;
    private readonly IMapper _mapper;

    public ProductQueryHandlers(
        IRepository<Product, Guid> products,
        IRepository<Category, Guid> categories,
        IRepository<Supplier, Guid> suppliers,
        IMapper mapper)
    {
        _products = products;
        _categories = categories;
        _suppliers = suppliers;
        _mapper = mapper;
    }

    public async Task<IPaginate<ProductListItemDto>> Handle(GetProductListQuery request, CancellationToken cancellationToken)
    {
        IQueryable<Product> query = _products.Query(enableTracking: false);

        if (!request.IncludeDiscontinued)
            query = query.Where(p => !p.IsDiscontinued);

        if (request.CategoryId is { } categoryId)
            query = query.Where(p => p.CategoryId == categoryId);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string pattern = $"%{request.Search.Trim().ToUpperInvariant()}%";
            query = query.Where(p => EF.Functions.Like(p.Name.ToUpper(), pattern));
        }

        IQueryable<Category> categories = _categories.Query(withDeleted: true, enableTracking: false);
        IQueryable<Supplier> suppliers = _suppliers.Query(withDeleted: true, enableTracking: false);

        return await query
            .OrderBy(p => p.Name)
            .Select(p => new ProductListItemDto(
                p.Id,
                p.Name,
                categories.Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefault(),
                suppliers.Where(s => s.Id == p.SupplierId).Select(s => s.CompanyName).FirstOrDefault(),
                p.QuantityPerUnit,
                p.UnitPrice,
                p.UnitsInStock,
                p.IsDiscontinued
            ))
            .ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken);
    }

    public async Task<IPaginate<ProductDto>> Handle(SearchProductsQuery request, CancellationToken cancellationToken)
    {
        IPaginate<Product> page = await DynamicSearch
            .Apply(_products.Query(enableTracking: false), request.Query, q => q.OrderBy(p => p.Name))
            .ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken);

        return page.Map(p => _mapper.Map<Product, ProductDto>(p)!);
    }

    public async Task<ProductDto> Handle(GetProductByIdQuery request, CancellationToken cancellationToken)
    {
        Product product =
            await _products.GetByIdAsync(request.Id, enableTracking: false, cancellationToken: cancellationToken)
            ?? throw NotFoundException.For<Product>(request.Id);

        return _mapper.Map<Product, ProductDto>(product)!;
    }

    public async Task<IReadOnlyList<ProductDto>> Handle(GetProductsToReorderQuery request, CancellationToken cancellationToken)
    {
        IPaginate<Product> page = await _products.GetListAsync(
            p => !p.IsDiscontinued && p.UnitsInStock + p.UnitsOnOrder <= p.ReorderLevel,
            orderBy: q => q.OrderBy(p => p.UnitsInStock),
            size: PageRequest.MaxSize,
            enableTracking: false,
            cancellationToken: cancellationToken
        );

        return page.Items.Select(p => _mapper.Map<Product, ProductDto>(p)!).ToList();
    }
}
