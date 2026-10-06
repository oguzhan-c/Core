using Can.Core.Application.Rules;
using Can.Core.Domain.Results;
using Can.Core.Persistence.Repositories;
using Northwind.Application.Features.Categories;
using Northwind.Domain.Catalog;

namespace Northwind.Application.Features.Products;

public sealed class ProductBusinessRules : BaseBusinessRules
{
    private readonly IRepository<Product, Guid> _products;
    private readonly IRepository<Category, Guid> _categories;
    private readonly IRepository<Supplier, Guid> _suppliers;

    public ProductBusinessRules(IRepository<Product, Guid> products, IRepository<Category, Guid> categories, IRepository<Supplier, Guid> suppliers)
    {
        _products = products;
        _categories = categories;
        _suppliers = suppliers;
    }

    public Task<Result<Product>> MustExistAsync(Guid id, CancellationToken cancellationToken) =>
        _products.GetByIdAsync(id, cancellationToken: cancellationToken).ToResult(ProductErrors.NotFound(id));

    public async Task<Result<Success>> NameMustBeUniqueAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        string trimmed = name.Trim();
        return await _products.AnyAsync(p => p.Name == trimmed && p.Id != exceptId, cancellationToken: cancellationToken)
            ? Error.Conflict("product.duplicate_name", $"'{trimmed}' adında bir ürün zaten var.")
            : Result.Success;
    }

    public async Task<Result<Success>> ReferencesMustExistAsync(Guid? categoryId, Guid? supplierId, CancellationToken cancellationToken)
    {
        if (categoryId is { } c && !await _categories.AnyAsync(x => x.Id == c, cancellationToken: cancellationToken))
            return CategoryErrors.NotFound(c);

        if (supplierId is { } s && !await _suppliers.AnyAsync(x => x.Id == s, cancellationToken: cancellationToken))
            return Error.NotFound("supplier.not_found", $"'{s}' tedarikçisi bulunamadı.");

        return Result.Success;
    }
}
