using Can.Core.Application;
using Can.Core.Application.Rules;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Domain.Catalog;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Categories;

// ---------------------------------------------------------------- modeller

public sealed record CategoryDto(Guid Id, string Name, string? Description, int ProductCount);

/// <summary>Kategori listesi önbellek etiketi (ürün eklenip silinince de temizlenir: ürün sayıları değişir).</summary>
public static class CategoryCacheTags
{
    public const string Categories = "categories";
}

// ---------------------------------------------------------------- kurallar

public sealed class CategoryBusinessRules : BaseBusinessRules
{
    private readonly IRepository<Category, Guid> _categories;
    private readonly IRepository<Product, Guid> _products;

    public CategoryBusinessRules(IRepository<Category, Guid> categories, IRepository<Product, Guid> products)
    {
        _categories = categories;
        _products = products;
    }

    public async Task<Result<Success>> NameMustBeUniqueAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        string trimmed = name.Trim();
        return await _categories.AnyAsync(c => c.Name == trimmed && c.Id != exceptId, cancellationToken: cancellationToken)
            ? Error.Conflict("category.duplicate_name", $"'{trimmed}' adında bir kategori zaten var.")
            : Result.Success;
    }

    public Task<Result<Category>> MustExistAsync(Guid id, CancellationToken cancellationToken) =>
        _categories.GetByIdAsync(id, cancellationToken: cancellationToken).ToResult(CategoryErrors.NotFound(id));

    public async Task<Result<Success>> MustHaveNoProductsAsync(Guid id, CancellationToken cancellationToken) =>
        await _products.AnyAsync(p => p.CategoryId == id, cancellationToken: cancellationToken) ? CategoryErrors.HasProducts : Result.Success;
}

public static class CategoryErrors
{
    public static readonly Error HasProducts =
        Error.Failure("category.has_products", "Ürünü olan kategori silinemez; önce ürünleri başka kategoriye taşı.");

    public static Error NotFound(Guid id) => Error.NotFound("category.not_found", $"'{id}' kategorisi bulunamadı.");
}

// ---------------------------------------------------------------- command'lar

public sealed record CreateCategoryCommand(string Name, string? Description)
    : IRequest<Result<Guid>>, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
    public IReadOnlyCollection<string> CacheTagsToRemove => [CategoryCacheTags.Categories];
}

public sealed record UpdateCategoryCommand(Guid Id, string Name, string? Description)
    : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
    public IReadOnlyCollection<string> CacheTagsToRemove => [CategoryCacheTags.Categories];
}

public sealed record DeleteCategoryCommand(Guid Id) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
    public IReadOnlyCollection<string> CacheTagsToRemove => [CategoryCacheTags.Categories];
}

public sealed class CreateCategoryCommandValidator : AbstractValidator<CreateCategoryCommand>
{
    public CreateCategoryCommandValidator()
    {
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Category.NameMaxLength);
        RuleFor(c => c.Description).MaximumLength(Category.DescriptionMaxLength);
    }
}

public sealed class UpdateCategoryCommandValidator : AbstractValidator<UpdateCategoryCommand>
{
    public UpdateCategoryCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Name).NotEmpty().MaximumLength(Category.NameMaxLength);
        RuleFor(c => c.Description).MaximumLength(Category.DescriptionMaxLength);
    }
}

public sealed class CategoryCommandHandlers
    : IRequestHandler<CreateCategoryCommand, Result<Guid>>,
        IRequestHandler<UpdateCategoryCommand, Result<Success>>,
        IRequestHandler<DeleteCategoryCommand, Result<Success>>
{
    private readonly IRepository<Category, Guid> _categories;
    private readonly CategoryBusinessRules _rules;

    public CategoryCommandHandlers(IRepository<Category, Guid> categories, CategoryBusinessRules rules)
    {
        _categories = categories;
        _rules = rules;
    }

    public async Task<Result<Guid>> Handle(CreateCategoryCommand request, CancellationToken cancellationToken)
    {
        Result<Success> unique = await _rules.NameMustBeUniqueAsync(request.Name, null, cancellationToken);
        if (unique.IsFailure)
            return unique.Errors;

        return await Category
            .Create(request.Name, request.Description)
            .TapAsync(category => _categories.AddAsync(category, cancellationToken))
            .Map(category => category.Id);
    }

    public async Task<Result<Success>> Handle(UpdateCategoryCommand request, CancellationToken cancellationToken)
    {
        Result<Category> category = await _rules.MustExistAsync(request.Id, cancellationToken);
        if (category.IsFailure)
            return category.Errors;

        Result<Success> unique = await _rules.NameMustBeUniqueAsync(request.Name, request.Id, cancellationToken);
        return unique.IsFailure ? unique : category.Value.Update(request.Name, request.Description);
    }

    public async Task<Result<Success>> Handle(DeleteCategoryCommand request, CancellationToken cancellationToken)
    {
        Result<Category> category = await _rules.MustExistAsync(request.Id, cancellationToken);
        if (category.IsFailure)
            return category.Errors;

        Result<Success> empty = await _rules.MustHaveNoProductsAsync(request.Id, cancellationToken);
        if (empty.IsFailure)
            return empty;

        _categories.Delete(category.Value);
        return Result.Success;
    }
}

// ---------------------------------------------------------------- query'ler

/// <summary>Tüm kategoriler, ürün sayılarıyla. Önbelleğe alınır; kategori değişince temizlenir.</summary>
public sealed record GetCategoryListQuery : IRequest<Result<IReadOnlyList<CategoryDto>>>, ISecuredRequest, ICachableRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
    public string CacheKey => "categories:list";
    public IReadOnlyCollection<string> CacheTags => [CategoryCacheTags.Categories];
}

public sealed record GetCategoryByIdQuery(Guid Id) : IRequest<Result<CategoryDto>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

public sealed class CategoryQueryHandlers
    : IRequestHandler<GetCategoryListQuery, Result<IReadOnlyList<CategoryDto>>>,
        IRequestHandler<GetCategoryByIdQuery, Result<CategoryDto>>
{
    private readonly IRepository<Category, Guid> _categories;
    private readonly IRepository<Product, Guid> _products;

    public CategoryQueryHandlers(IRepository<Category, Guid> categories, IRepository<Product, Guid> products)
    {
        _categories = categories;
        _products = products;
    }

    public async Task<Result<IReadOnlyList<CategoryDto>>> Handle(GetCategoryListQuery request, CancellationToken cancellationToken) =>
        // Sıralama projeksiyondan ÖNCE: EF, DTO'nun (constructor ile oluşan) alanına göre sıralamayı SQL'e çeviremez.
        Result.Ok<IReadOnlyList<CategoryDto>>(
            await Project(_categories.Query(enableTracking: false).OrderBy(c => c.Name)).ToListAsync(cancellationToken)
        );

    public Task<Result<CategoryDto>> Handle(GetCategoryByIdQuery request, CancellationToken cancellationToken) =>
        Project(_categories.Query(enableTracking: false).Where(c => c.Id == request.Id))
            .FirstOrDefaultAsync(cancellationToken)
            .ToResult(CategoryErrors.NotFound(request.Id));

    private IQueryable<CategoryDto> Project(IQueryable<Category> categories)
    {
        IQueryable<Product> products = _products.Query(enableTracking: false);
        return categories.Select(c => new CategoryDto(c.Id, c.Name, c.Description, products.Count(p => p.CategoryId == c.Id)));
    }
}
