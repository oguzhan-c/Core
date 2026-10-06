using Can.Core.Domain.Results;
using Northwind.Domain.Common;

namespace Northwind.Domain.Catalog;

public sealed class Category : TenantAggregateRoot
{
    public const int NameMaxLength = 50;
    public const int DescriptionMaxLength = 500;

    private Category()
    {
        Name = string.Empty;
    }

    private Category(Guid id)
        : base(id)
    {
        Name = string.Empty;
    }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public static Result<Category> Create(string name, string? description)
    {
        var category = new Category(Guid.CreateVersion7());
        return category.Update(name, description).Map(_ => category);
    }

    public Result<Success> Update(string name, string? description)
    {
        Result<Success> valid = Result.Validate(
            Check.Required(name, "Kategori adı", NameMaxLength),
            Check.Optional(description, "Açıklama", DescriptionMaxLength)
        );
        if (valid.IsFailure)
            return valid;

        Name = Check.Clean(name);
        Description = Check.CleanOptional(description);
        return Result.Success;
    }
}
