using Can.Core.Mapping;
using Northwind.Domain.Catalog;

namespace Northwind.Application.Features.Products;

public sealed record ProductDto(
    Guid Id,
    string Name,
    Guid? CategoryId,
    Guid? SupplierId,
    string? QuantityPerUnit,
    decimal UnitPrice,
    int UnitsInStock,
    int UnitsOnOrder,
    int ReorderLevel,
    bool IsDiscontinued,
    bool NeedsReorder);

public sealed record ProductListItemDto(
    Guid Id,
    string Name,
    string? CategoryName,
    string? SupplierName,
    string? QuantityPerUnit,
    decimal UnitPrice,
    int UnitsInStock,
    bool IsDiscontinued);

public sealed class ProductProfile : MappingProfile
{
    public ProductProfile()
    {
        // NeedsReorder hesaplanan bir özellik: yalnızca bellekte (Map) kullanılır, ProjectTo ile değil.
        CreateMap<Product, ProductDto>();
    }
}
