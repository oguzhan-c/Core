using Can.Core.Domain.Auditing;
using Can.Core.Domain.Results;
using Northwind.Domain.Common;

namespace Northwind.Domain.Catalog;

/// <summary>Satılan ürün. Fiyat ve stok değişiklikleri değişiklik geçmişine (audit trail) yazılır.</summary>
[Audited]
public sealed class Product : TenantAggregateRoot
{
    public const int NameMaxLength = 60;
    public const int QuantityPerUnitMaxLength = 40;

    private Product()
    {
        Name = string.Empty;
    }

    private Product(Guid id)
        : base(id)
    {
        Name = string.Empty;
    }

    public string Name { get; private set; }

    public Guid? CategoryId { get; private set; }

    public Guid? SupplierId { get; private set; }

    /// <summary>Satış birimi, ör. "24 - 12 oz bottles".</summary>
    public string? QuantityPerUnit { get; private set; }

    public decimal UnitPrice { get; private set; }

    public int UnitsInStock { get; private set; }

    /// <summary>Tedarikçiden sipariş edilmiş, henüz gelmemiş miktar.</summary>
    public int UnitsOnOrder { get; private set; }

    /// <summary>Stok bu seviyeye inince yeniden sipariş verilmeli.</summary>
    public int ReorderLevel { get; private set; }

    public bool IsDiscontinued { get; private set; }

    public bool NeedsReorder => !IsDiscontinued && UnitsInStock + UnitsOnOrder <= ReorderLevel;

    public static Result<Product> Create(
        string name,
        Guid? categoryId,
        Guid? supplierId,
        string? quantityPerUnit,
        decimal unitPrice,
        int unitsInStock,
        int reorderLevel)
    {
        Result<Success> valid = Result.Validate(
            Check.NotNegative(unitPrice, "Birim fiyat"),
            Check.NotNegative(unitsInStock, "Stok")
        );
        if (valid.IsFailure)
            return valid.Errors;

        var product = new Product(Guid.CreateVersion7()) { UnitPrice = unitPrice, UnitsInStock = unitsInStock };
        return product.UpdateDetails(name, categoryId, supplierId, quantityPerUnit, reorderLevel).Map(_ => product);
    }

    /// <summary>Dış kaynaktan (Northwind SQL) aktarım: siparişteki miktar ve satış durumu da verilir.</summary>
    public static Result<Product> Import(
        string name,
        Guid? categoryId,
        Guid? supplierId,
        string? quantityPerUnit,
        decimal unitPrice,
        int unitsInStock,
        int unitsOnOrder,
        int reorderLevel,
        bool isDiscontinued)
    {
        if (Check.NotNegative(unitsOnOrder, "Siparişteki miktar") is { } error)
            return error;

        return Create(name, categoryId, supplierId, quantityPerUnit, unitPrice, unitsInStock, reorderLevel)
            .Tap(product =>
            {
                product.UnitsOnOrder = unitsOnOrder;
                product.IsDiscontinued = isDiscontinued;
            });
    }

    public Result<Success> UpdateDetails(string name, Guid? categoryId, Guid? supplierId, string? quantityPerUnit, int reorderLevel)
    {
        Result<Success> valid = Result.Validate(
            Check.Required(name, "Ürün adı", NameMaxLength),
            Check.Optional(quantityPerUnit, "Birim", QuantityPerUnitMaxLength),
            Check.NotNegative(reorderLevel, "Yeniden sipariş seviyesi")
        );
        if (valid.IsFailure)
            return valid;

        Name = Check.Clean(name);
        CategoryId = categoryId;
        SupplierId = supplierId;
        QuantityPerUnit = Check.CleanOptional(quantityPerUnit);
        ReorderLevel = reorderLevel;
        return Result.Success;
    }

    /// <summary>Fiyatı değiştirir; değiştiyse <see cref="ProductPriceChanged"/> event'i yayınlanır.</summary>
    public Result<Success> ChangePrice(decimal newPrice)
    {
        if (Check.NotNegative(newPrice, "Birim fiyat") is { } error)
            return error;

        if (newPrice == UnitPrice)
            return Result.Success;

        decimal oldPrice = UnitPrice;
        UnitPrice = newPrice;
        RaiseDomainEvent(new ProductPriceChanged(Id, oldPrice, newPrice));
        return Result.Success;
    }

    /// <summary>Tedarikçiden gelen ürün: stok artar, siparişteki miktar düşer.</summary>
    public Result<Success> Restock(int quantity)
    {
        if (Check.Positive(quantity, "Miktar") is { } error)
            return error;

        UnitsInStock += quantity;
        UnitsOnOrder = Math.Max(0, UnitsOnOrder - quantity);
        return Result.Success;
    }

    /// <summary>Siparişe ayırır. Stok yeniden sipariş seviyesine düşerse <see cref="StockBelowReorderLevel"/> yayınlanır.</summary>
    public Result<Success> ReserveStock(int quantity)
    {
        if (Check.Positive(quantity, "Miktar") is { } error)
            return error;

        if (IsDiscontinued)
            return ProductErrors.Discontinued(Name);

        if (quantity > UnitsInStock)
            return ProductErrors.InsufficientStock(Name, UnitsInStock, quantity);

        UnitsInStock -= quantity;

        if (UnitsInStock <= ReorderLevel)
            RaiseDomainEvent(new StockBelowReorderLevel(Id, Name, UnitsInStock, ReorderLevel));

        return Result.Success;
    }

    /// <summary>İptal edilen siparişin ürünleri stoğa geri döner.</summary>
    public Result<Success> ReleaseStock(int quantity)
    {
        if (Check.Positive(quantity, "Miktar") is { } error)
            return error;

        UnitsInStock += quantity;
        return Result.Success;
    }

    public void Discontinue()
    {
        if (IsDiscontinued)
            return;

        IsDiscontinued = true;
        RaiseDomainEvent(new ProductDiscontinued(Id, Name));
    }
}

/// <summary>Ürün hataları.</summary>
public static class ProductErrors
{
    public static Error NotFound(Guid id) => Error.NotFound("product.not_found", $"'{id}' ürünü bulunamadı.");

    public static Error Discontinued(string name) =>
        Error.Failure("product.discontinued", $"'{name}' satıştan kaldırılmış; siparişe eklenemez.");

    public static Error InsufficientStock(string name, int inStock, int requested) =>
        Error.Failure("product.insufficient_stock", $"'{name}' için yeterli stok yok (stok: {inStock}, istenen: {requested}).")
            .WithMetadata("inStock", inStock)
            .WithMetadata("requested", requested);
}
