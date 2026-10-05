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

    private Category(Guid id, string name, string? description)
        : base(id)
    {
        Name = string.Empty;
        Update(name, description);
    }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public static Category Create(string name, string? description) => new(Guid.CreateVersion7(), name, description);

    public void Update(string name, string? description)
    {
        Name = Check.Required(name, "Kategori adı", NameMaxLength);
        Description = Check.Optional(description, "Açıklama", DescriptionMaxLength);
    }
}
