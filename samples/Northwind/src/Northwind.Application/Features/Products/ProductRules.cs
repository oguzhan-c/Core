using Can.Core.Application.Exceptions;
using Can.Core.Application.Rules;
using Can.Core.Persistence.Repositories;
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

    public async Task<Product> MustExistAsync(Guid id, CancellationToken cancellationToken) =>
        await _products.GetByIdAsync(id, cancellationToken: cancellationToken) ?? throw NotFoundException.For<Product>(id);

    public async Task NameMustBeUniqueAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        string trimmed = name.Trim();
        if (await _products.AnyAsync(p => p.Name == trimmed && p.Id != exceptId, cancellationToken: cancellationToken))
            throw new ConflictException($"'{trimmed}' adında bir ürün zaten var.");
    }

    public async Task ReferencesMustExistAsync(Guid? categoryId, Guid? supplierId, CancellationToken cancellationToken)
    {
        if (categoryId is { } c && !await _categories.AnyAsync(x => x.Id == c, cancellationToken: cancellationToken))
            throw NotFoundException.For<Category>(c);

        if (supplierId is { } s && !await _suppliers.AnyAsync(x => x.Id == s, cancellationToken: cancellationToken))
            throw NotFoundException.For<Supplier>(s);
    }
}
