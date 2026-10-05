namespace Can.Core.Mapping.Tests;

// ------------------------------------------------------------ Kaynak (entity) tipleri

public enum ProductStatus
{
    Draft,
    Active,
}

public class Brand
{
    public string Name { get; set; } = "";
}

public class Tag
{
    public string Name { get; set; } = "";
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public ProductStatus Status { get; set; }
    public Brand? Brand { get; set; }
    public List<Tag> Tags { get; set; } = [];
    public int? Stock { get; set; }
    public string? InternalNote { get; set; }
}

public class Address
{
    public string City { get; set; } = "";
}

public class Customer
{
    public int Id { get; set; }
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public Address? Address { get; set; }
}

public class Category
{
    public string Name { get; set; } = "";
    public Category? Parent { get; set; }
}

// ------------------------------------------------------------ Hedef (DTO / command) tipleri

public class TagDto
{
    public string Name { get; set; } = "";
}

public class ProductDto
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public string Status { get; set; } = "";
    public string? BrandName { get; set; }
    public List<TagDto> Tags { get; set; } = [];
    public int Stock { get; set; }
}

/// <summary>Positional record: constructor ile oluşturulur.</summary>
public sealed record ProductSummary(int Id, string Name)
{
    public string? BrandName { get; init; }
}

public class CustomerDto
{
    public int Id { get; set; }
    public string FullName { get; set; } = "";
    public string? AddressCity { get; set; }
    public string? Secret { get; set; }
}

public class CategoryDto
{
    public string Name { get; set; } = "";
    public CategoryDto? Parent { get; set; }
}

public class UpdateProductCommand
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

public class ProductEditModel
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}

public class Unmappable
{
    public int Id { get; set; }
    public string NotInSource { get; set; } = "";
}
