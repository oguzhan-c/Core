using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.MultiTenancy;
using Can.Core.Persistence.Paging;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Common;
using Northwind.Domain.Catalog;

namespace Northwind.Application.Features.Store;

// Herkese açık mağaza kataloğu. Giriş gerekmez; mağaza adresteki kısa addan seçilir (yalnızca okunur, herkese
// açık veriler). Giriş yapılmış isteklerde mağaza yine yalnızca token'dan gelir.

public sealed record StoreTenantDto(string Identifier, string Name);

public sealed record StoreCategoryDto(Guid Id, string Name, string? Description, int ProductCount);

public sealed record StoreProductDto(
    Guid Id,
    string Name,
    Guid? CategoryId,
    string? CategoryName,
    string? SupplierName,
    string? QuantityPerUnit,
    decimal UnitPrice,
    int UnitsInStock);

public enum StoreProductSort
{
    Name,
    PriceAsc,
    PriceDesc,
}

public sealed record GetStoreTenantsQuery : IRequest<Result<IReadOnlyList<StoreTenantDto>>>;

public sealed record GetStoreCategoriesQuery(string Tenant) : IRequest<Result<IReadOnlyList<StoreCategoryDto>>>;

public sealed record GetStoreProductsQuery(
    string Tenant,
    PageRequest Page,
    Guid? CategoryId = null,
    string? Search = null,
    StoreProductSort Sort = StoreProductSort.Name) : IRequest<Result<IPaginate<StoreProductDto>>>;

public sealed record GetStoreProductQuery(string Tenant, Guid Id) : IRequest<Result<StoreProductDto>>;

public sealed class GetStoreProductsQueryValidator : AbstractValidator<GetStoreProductsQuery>
{
    public GetStoreProductsQueryValidator()
    {
        RuleFor(q => q.Tenant).NotEmpty().MaximumLength(64);
        RuleFor(q => q.Page).ValidPage();
        RuleFor(q => q.Search).MaximumLength(100);
    }
}

public sealed class StoreCatalogHandlers
    : IRequestHandler<GetStoreTenantsQuery, Result<IReadOnlyList<StoreTenantDto>>>,
        IRequestHandler<GetStoreCategoriesQuery, Result<IReadOnlyList<StoreCategoryDto>>>,
        IRequestHandler<GetStoreProductsQuery, Result<IPaginate<StoreProductDto>>>,
        IRequestHandler<GetStoreProductQuery, Result<StoreProductDto>>
{
    private readonly ITenantStore _tenantStore;
    private readonly StoreTenant _storeTenant;
    private readonly IRepository<Product, Guid> _products;
    private readonly IRepository<Category, Guid> _categories;
    private readonly IRepository<Supplier, Guid> _suppliers;

    public StoreCatalogHandlers(
        ITenantStore tenantStore,
        StoreTenant storeTenant,
        IRepository<Product, Guid> products,
        IRepository<Category, Guid> categories,
        IRepository<Supplier, Guid> suppliers)
    {
        _tenantStore = tenantStore;
        _storeTenant = storeTenant;
        _products = products;
        _categories = categories;
        _suppliers = suppliers;
    }

    public async Task<Result<IReadOnlyList<StoreTenantDto>>> Handle(GetStoreTenantsQuery request, CancellationToken cancellationToken) =>
        Result.Ok<IReadOnlyList<StoreTenantDto>>(
            (await _tenantStore.GetAllAsync(cancellationToken))
                .Where(t => t.IsActive)
                .Select(t => new StoreTenantDto(t.Identifier, string.IsNullOrEmpty(t.Name) ? t.Identifier : t.Name))
                .ToList()
        );

    public async Task<Result<IReadOnlyList<StoreCategoryDto>>> Handle(GetStoreCategoriesQuery request, CancellationToken cancellationToken)
    {
        Result<Can.Core.MultiTenancy.TenantInfo> store = await _storeTenant.UseAsync(request.Tenant, cancellationToken);
        if (store.IsFailure)
            return store.Errors;

        IQueryable<Product> products = OnSale();

        List<StoreCategoryDto> categories = await _categories
            .Query(enableTracking: false)
            .OrderBy(c => c.Name)
            .Select(c => new StoreCategoryDto(c.Id, c.Name, c.Description, products.Count(p => p.CategoryId == c.Id)))
            .ToListAsync(cancellationToken);

        return Result.Ok<IReadOnlyList<StoreCategoryDto>>(categories);
    }

    public async Task<Result<IPaginate<StoreProductDto>>> Handle(GetStoreProductsQuery request, CancellationToken cancellationToken)
    {
        Result<Can.Core.MultiTenancy.TenantInfo> store = await _storeTenant.UseAsync(request.Tenant, cancellationToken);
        if (store.IsFailure)
            return store.Errors;

        IQueryable<Product> query = OnSale();

        if (request.CategoryId is { } categoryId)
            query = query.Where(p => p.CategoryId == categoryId);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string pattern = $"%{request.Search.Trim().ToUpperInvariant()}%";
            query = query.Where(p => EF.Functions.Like(p.Name.ToUpper(), pattern));
        }

        query = request.Sort switch
        {
            StoreProductSort.PriceAsc => query.OrderBy(p => p.UnitPrice).ThenBy(p => p.Name),
            StoreProductSort.PriceDesc => query.OrderByDescending(p => p.UnitPrice).ThenBy(p => p.Name),
            _ => query.OrderBy(p => p.Name),
        };

        return Result.Ok(await Project(query).ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken));
    }

    public async Task<Result<StoreProductDto>> Handle(GetStoreProductQuery request, CancellationToken cancellationToken)
    {
        Result<Can.Core.MultiTenancy.TenantInfo> store = await _storeTenant.UseAsync(request.Tenant, cancellationToken);
        if (store.IsFailure)
            return store.Errors;

        return await Project(OnSale().Where(p => p.Id == request.Id)).FirstOrDefaultAsync(cancellationToken).ToResult(ProductErrors.NotFound(request.Id));
    }

    private IQueryable<Product> OnSale() => _products.Query(enableTracking: false).Where(p => !p.IsDiscontinued);

    private IQueryable<StoreProductDto> Project(IQueryable<Product> products)
    {
        IQueryable<Category> categories = _categories.Query(withDeleted: true, enableTracking: false);
        IQueryable<Supplier> suppliers = _suppliers.Query(withDeleted: true, enableTracking: false);

        return products.Select(p => new StoreProductDto(
            p.Id,
            p.Name,
            p.CategoryId,
            categories.Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefault(),
            suppliers.Where(s => s.Id == p.SupplierId).Select(s => s.CompanyName).FirstOrDefault(),
            p.QuantityPerUnit,
            p.UnitPrice,
            p.UnitsInStock
        ));
    }
}
