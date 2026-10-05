using Can.Core.Application;
using Can.Core.Application.Exceptions;
using Can.Core.Application.Rules;
using Can.Core.Domain.Exceptions;
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

    public async Task NameMustBeUniqueAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        string trimmed = name.Trim();
        if (await _categories.AnyAsync(c => c.Name == trimmed && c.Id != exceptId, cancellationToken: cancellationToken))
            throw new ConflictException($"'{trimmed}' adında bir kategori zaten var.");
    }

    public async Task<Category> MustExistAsync(Guid id, CancellationToken cancellationToken) =>
        await _categories.GetByIdAsync(id, cancellationToken: cancellationToken) ?? throw NotFoundException.For<Category>(id);

    public async Task MustHaveNoProductsAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await _products.AnyAsync(p => p.CategoryId == id, cancellationToken: cancellationToken))
            throw new BusinessException("Ürünü olan kategori silinemez; önce ürünleri başka kategoriye taşı.");
    }
}

// ---------------------------------------------------------------- command'lar

public sealed record CreateCategoryCommand(string Name, string? Description)
    : IRequest<Guid>, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
    public IReadOnlyCollection<string> CacheTagsToRemove => [CategoryCacheTags.Categories];
}

public sealed record UpdateCategoryCommand(Guid Id, string Name, string? Description)
    : IRequest, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
    public IReadOnlyCollection<string> CacheTagsToRemove => [CategoryCacheTags.Categories];
}

public sealed record DeleteCategoryCommand(Guid Id) : IRequest, ISecuredRequest, ITransactionalRequest, ICacheRemoverRequest
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
    : IRequestHandler<CreateCategoryCommand, Guid>,
        IRequestHandler<UpdateCategoryCommand>,
        IRequestHandler<DeleteCategoryCommand>
{
    private readonly IRepository<Category, Guid> _categories;
    private readonly CategoryBusinessRules _rules;

    public CategoryCommandHandlers(IRepository<Category, Guid> categories, CategoryBusinessRules rules)
    {
        _categories = categories;
        _rules = rules;
    }

    public async Task<Guid> Handle(CreateCategoryCommand request, CancellationToken cancellationToken)
    {
        await _rules.NameMustBeUniqueAsync(request.Name, null, cancellationToken);

        Category category = Category.Create(request.Name, request.Description);
        await _categories.AddAsync(category, cancellationToken);
        return category.Id;
    }

    public async Task Handle(UpdateCategoryCommand request, CancellationToken cancellationToken)
    {
        Category category = await _rules.MustExistAsync(request.Id, cancellationToken);
        await _rules.NameMustBeUniqueAsync(request.Name, request.Id, cancellationToken);
        category.Update(request.Name, request.Description);
    }

    public async Task Handle(DeleteCategoryCommand request, CancellationToken cancellationToken)
    {
        Category category = await _rules.MustExistAsync(request.Id, cancellationToken);
        await _rules.MustHaveNoProductsAsync(request.Id, cancellationToken);
        _categories.Delete(category);
    }
}

// ---------------------------------------------------------------- query'ler

/// <summary>Tüm kategoriler, ürün sayılarıyla. Önbelleğe alınır; kategori değişince temizlenir.</summary>
public sealed record GetCategoryListQuery : IRequest<IReadOnlyList<CategoryDto>>, ISecuredRequest, ICachableRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
    public string CacheKey => "categories:list";
    public IReadOnlyCollection<string> CacheTags => [CategoryCacheTags.Categories];
}

public sealed record GetCategoryByIdQuery(Guid Id) : IRequest<CategoryDto>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

public sealed class CategoryQueryHandlers
    : IRequestHandler<GetCategoryListQuery, IReadOnlyList<CategoryDto>>,
        IRequestHandler<GetCategoryByIdQuery, CategoryDto>
{
    private readonly IRepository<Category, Guid> _categories;
    private readonly IRepository<Product, Guid> _products;

    public CategoryQueryHandlers(IRepository<Category, Guid> categories, IRepository<Product, Guid> products)
    {
        _categories = categories;
        _products = products;
    }

    public async Task<IReadOnlyList<CategoryDto>> Handle(GetCategoryListQuery request, CancellationToken cancellationToken) =>
        // Sıralama projeksiyondan ÖNCE: EF, DTO'nun (constructor ile oluşan) alanına göre sıralamayı SQL'e çeviremez.
        await Project(_categories.Query(enableTracking: false).OrderBy(c => c.Name)).ToListAsync(cancellationToken);

    public async Task<CategoryDto> Handle(GetCategoryByIdQuery request, CancellationToken cancellationToken) =>
        await Project(_categories.Query(enableTracking: false).Where(c => c.Id == request.Id)).FirstOrDefaultAsync(cancellationToken)
        ?? throw NotFoundException.For<Category>(request.Id);

    private IQueryable<CategoryDto> Project(IQueryable<Category> categories)
    {
        IQueryable<Product> products = _products.Query(enableTracking: false);
        return categories.Select(c => new CategoryDto(c.Id, c.Name, c.Description, products.Count(p => p.CategoryId == c.Id)));
    }
}
