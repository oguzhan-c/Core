using Can.Core.Application;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using Northwind.Application.Features.Categories;
using Northwind.Domain.Catalog;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Products;

public sealed record CreateProductCommand(
    string Name,
    Guid? CategoryId,
    Guid? SupplierId,
    string? QuantityPerUnit,
    decimal UnitPrice,
    int UnitsInStock,
    int ReorderLevel) : IRequest<Guid>, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
    public IReadOnlyCollection<string> CacheTagsToRemove => [CategoryCacheTags.Categories];
}

public sealed record UpdateProductCommand(Guid Id, string Name, Guid? CategoryId, Guid? SupplierId, string? QuantityPerUnit, int ReorderLevel)
    : IRequest, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
    public IReadOnlyCollection<string> CacheTagsToRemove => [CategoryCacheTags.Categories];
}

/// <summary>Fiyat değişikliği; değişiklik geçmişine eski/yeni fiyat yazılır.</summary>
public sealed record ChangeProductPriceCommand(Guid Id, decimal UnitPrice) : IRequest, ISecuredRequest, ITransactionalRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
}

/// <summary>Depoya gelen ürünü stoğa ekler.</summary>
public sealed record RestockProductCommand(Guid Id, int Quantity) : IRequest, ISecuredRequest, ITransactionalRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Warehouse];
}

/// <summary>Ürünü satıştan kaldırır; <see cref="ProductDiscontinued"/> outbox ile yayınlanır.</summary>
public sealed record DiscontinueProductCommand(Guid Id) : IRequest, ISecuredRequest, ITransactionalRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
}

public sealed record DeleteProductCommand(Guid Id) : IRequest, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
    public IReadOnlyCollection<string> CacheTagsToRemove => [CategoryCacheTags.Categories];
}

public sealed class CreateProductCommandValidator : AbstractValidator<CreateProductCommand>
{
    public CreateProductCommandValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Product.NameMaxLength);
        RuleFor(c => c.QuantityPerUnit).MaximumLength(Product.QuantityPerUnitMaxLength);
        RuleFor(c => c.UnitPrice).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, ignoreTrailingZeros: true);
        RuleFor(c => c.UnitsInStock).GreaterThanOrEqualTo(0);
        RuleFor(c => c.ReorderLevel).GreaterThanOrEqualTo(0);
    }
}

public sealed class UpdateProductCommandValidator : AbstractValidator<UpdateProductCommand>
{
    public UpdateProductCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Product.NameMaxLength);
        RuleFor(c => c.QuantityPerUnit).MaximumLength(Product.QuantityPerUnitMaxLength);
        RuleFor(c => c.ReorderLevel).GreaterThanOrEqualTo(0);
    }
}

public sealed class ChangeProductPriceCommandValidator : AbstractValidator<ChangeProductPriceCommand>
{
    public ChangeProductPriceCommandValidator() =>
        RuleFor(c => c.UnitPrice).GreaterThanOrEqualTo(0).PrecisionScale(18, 2, ignoreTrailingZeros: true);
}

public sealed class RestockProductCommandValidator : AbstractValidator<RestockProductCommand>
{
    public RestockProductCommandValidator() => RuleFor(c => c.Quantity).InclusiveBetween(1, 100_000);
}

public sealed class ProductCommandHandlers
    : IRequestHandler<CreateProductCommand, Guid>,
        IRequestHandler<UpdateProductCommand>,
        IRequestHandler<ChangeProductPriceCommand>,
        IRequestHandler<RestockProductCommand>,
        IRequestHandler<DiscontinueProductCommand>,
        IRequestHandler<DeleteProductCommand>
{
    private readonly IRepository<Product, Guid> _products;
    private readonly ProductBusinessRules _rules;

    public ProductCommandHandlers(IRepository<Product, Guid> products, ProductBusinessRules rules)
    {
        _products = products;
        _rules = rules;
    }

    public async Task<Guid> Handle(CreateProductCommand request, CancellationToken cancellationToken)
    {
        await _rules.NameMustBeUniqueAsync(request.Name, null, cancellationToken);
        await _rules.ReferencesMustExistAsync(request.CategoryId, request.SupplierId, cancellationToken);

        Product product = Product.Create(
            request.Name,
            request.CategoryId,
            request.SupplierId,
            request.QuantityPerUnit,
            request.UnitPrice,
            request.UnitsInStock,
            request.ReorderLevel
        );

        await _products.AddAsync(product, cancellationToken);
        return product.Id;
    }

    public async Task Handle(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        Product product = await _rules.MustExistAsync(request.Id, cancellationToken);
        await _rules.NameMustBeUniqueAsync(request.Name, request.Id, cancellationToken);
        await _rules.ReferencesMustExistAsync(request.CategoryId, request.SupplierId, cancellationToken);

        product.UpdateDetails(request.Name, request.CategoryId, request.SupplierId, request.QuantityPerUnit, request.ReorderLevel);
    }

    public async Task Handle(ChangeProductPriceCommand request, CancellationToken cancellationToken) =>
        (await _rules.MustExistAsync(request.Id, cancellationToken)).ChangePrice(request.UnitPrice);

    public async Task Handle(RestockProductCommand request, CancellationToken cancellationToken) =>
        (await _rules.MustExistAsync(request.Id, cancellationToken)).Restock(request.Quantity);

    public async Task Handle(DiscontinueProductCommand request, CancellationToken cancellationToken) =>
        (await _rules.MustExistAsync(request.Id, cancellationToken)).Discontinue();

    public async Task Handle(DeleteProductCommand request, CancellationToken cancellationToken) =>
        _products.Delete(await _rules.MustExistAsync(request.Id, cancellationToken));
}
