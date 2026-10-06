using Can.Core.Application;
using Can.Core.Domain.Results;
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
    int ReorderLevel) : IRequest<Result<Guid>>, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
    public IReadOnlyCollection<string> CacheTagsToRemove => [CategoryCacheTags.Categories];
}

public sealed record UpdateProductCommand(Guid Id, string Name, Guid? CategoryId, Guid? SupplierId, string? QuantityPerUnit, int ReorderLevel)
    : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
    public IReadOnlyCollection<string> CacheTagsToRemove => [CategoryCacheTags.Categories];
}

/// <summary>Fiyat değişikliği; değişiklik geçmişine eski/yeni fiyat yazılır.</summary>
public sealed record ChangeProductPriceCommand(Guid Id, decimal UnitPrice) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
}

/// <summary>Depoya gelen ürünü stoğa ekler.</summary>
public sealed record RestockProductCommand(Guid Id, int Quantity) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest
{
    public IReadOnlyCollection<string> Permissions => [Northwind.Domain.Identity.Permissions.ProductsStock];
}

/// <summary>Ürünü satıştan kaldırır; <see cref="ProductDiscontinued"/> outbox ile yayınlanır.</summary>
public sealed record DiscontinueProductCommand(Guid Id) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
}

public sealed record DeleteProductCommand(Guid Id) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
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
    : IRequestHandler<CreateProductCommand, Result<Guid>>,
        IRequestHandler<UpdateProductCommand, Result<Success>>,
        IRequestHandler<ChangeProductPriceCommand, Result<Success>>,
        IRequestHandler<RestockProductCommand, Result<Success>>,
        IRequestHandler<DiscontinueProductCommand, Result<Success>>,
        IRequestHandler<DeleteProductCommand, Result<Success>>
{
    private readonly IRepository<Product, Guid> _products;
    private readonly ProductBusinessRules _rules;

    public ProductCommandHandlers(IRepository<Product, Guid> products, ProductBusinessRules rules)
    {
        _products = products;
        _rules = rules;
    }

    public async Task<Result<Guid>> Handle(CreateProductCommand request, CancellationToken cancellationToken)
    {
        Result<Success> rules = Result.Combine(
            await _rules.NameMustBeUniqueAsync(request.Name, null, cancellationToken),
            await _rules.ReferencesMustExistAsync(request.CategoryId, request.SupplierId, cancellationToken)
        );
        if (rules.IsFailure)
            return rules.Errors;

        return await Product
            .Create(
                request.Name,
                request.CategoryId,
                request.SupplierId,
                request.QuantityPerUnit,
                request.UnitPrice,
                request.UnitsInStock,
                request.ReorderLevel
            )
            .TapAsync(product => _products.AddAsync(product, cancellationToken))
            .Map(product => product.Id);
    }

    public async Task<Result<Success>> Handle(UpdateProductCommand request, CancellationToken cancellationToken)
    {
        Result<Product> product = await _rules.MustExistAsync(request.Id, cancellationToken);
        if (product.IsFailure)
            return product.Errors;

        Result<Success> rules = Result.Combine(
            await _rules.NameMustBeUniqueAsync(request.Name, request.Id, cancellationToken),
            await _rules.ReferencesMustExistAsync(request.CategoryId, request.SupplierId, cancellationToken)
        );
        if (rules.IsFailure)
            return rules;

        return product.Value.UpdateDetails(request.Name, request.CategoryId, request.SupplierId, request.QuantityPerUnit, request.ReorderLevel);
    }

    public Task<Result<Success>> Handle(ChangeProductPriceCommand request, CancellationToken cancellationToken) =>
        _rules.MustExistAsync(request.Id, cancellationToken).Then(product => product.ChangePrice(request.UnitPrice));

    public Task<Result<Success>> Handle(RestockProductCommand request, CancellationToken cancellationToken) =>
        _rules.MustExistAsync(request.Id, cancellationToken).Then(product => product.Restock(request.Quantity));

    public Task<Result<Success>> Handle(DiscontinueProductCommand request, CancellationToken cancellationToken) =>
        _rules.MustExistAsync(request.Id, cancellationToken)
            .Map(product =>
            {
                product.Discontinue();
                return Result.Success;
            });

    public Task<Result<Success>> Handle(DeleteProductCommand request, CancellationToken cancellationToken) =>
        _rules.MustExistAsync(request.Id, cancellationToken)
            .Map(product =>
            {
                _products.Delete(product);
                return Result.Success;
            });
}
