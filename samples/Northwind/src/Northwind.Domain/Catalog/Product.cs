using Can.Core.Domain.Auditing;
using Can.Core.Domain.Exceptions;
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

    public static Product Create(
        string name,
        Guid? categoryId,
        Guid? supplierId,
        string? quantityPerUnit,
        decimal unitPrice,
        int unitsInStock,
        int reorderLevel)
    {
        var product = new Product(Guid.CreateVersion7())
        {
            UnitPrice = Check.NotNegative(unitPrice, "Birim fiyat"),
            UnitsInStock = Check.NotNegative(unitsInStock, "Stok"),
        };

        product.UpdateDetails(name, categoryId, supplierId, quantityPerUnit, reorderLevel);
        return product;
    }

    /// <summary>Hazır veriyi (Northwind) içe aktarırken: event üretmez.</summary>
    public static Product Import(
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
        Product product = Create(name, categoryId, supplierId, quantityPerUnit, unitPrice, unitsInStock, reorderLevel);
        product.UnitsOnOrder = Check.NotNegative(unitsOnOrder, "Siparişteki miktar");
        product.IsDiscontinued = isDiscontinued;
        return product;
    }

    public void UpdateDetails(string name, Guid? categoryId, Guid? supplierId, string? quantityPerUnit, int reorderLevel)
    {
        Name = Check.Required(name, "Ürün adı", NameMaxLength);
        CategoryId = categoryId;
        SupplierId = supplierId;
        QuantityPerUnit = Check.Optional(quantityPerUnit, "Birim", QuantityPerUnitMaxLength);
        ReorderLevel = Check.NotNegative(reorderLevel, "Yeniden sipariş seviyesi");
    }

    public void ChangePrice(decimal newPrice)
    {
        Check.NotNegative(newPrice, "Birim fiyat");
        if (newPrice == UnitPrice)
            return;

        decimal oldPrice = UnitPrice;
        UnitPrice = newPrice;
        RaiseDomainEvent(new ProductPriceChanged(Id, oldPrice, newPrice));
    }

    /// <summary>Tedarikçiden gelen ürünü stoğa ekler.</summary>
    public void Restock(int quantity)
    {
        Check.Positive(quantity, "Miktar");
        UnitsInStock += quantity;
        UnitsOnOrder = Math.Max(0, UnitsOnOrder - quantity);
    }

    /// <summary>Sipariş için stoktan düşer.</summary>
    public void ReserveStock(int quantity)
    {
        Check.Positive(quantity, "Miktar");

        if (IsDiscontinued)
            throw new BusinessException($"'{Name}' satıştan kaldırılmış; siparişe eklenemez.");

        if (quantity > UnitsInStock)
            throw new BusinessException($"'{Name}' için yeterli stok yok (stok: {UnitsInStock}, istenen: {quantity}).");

        UnitsInStock -= quantity;

        if (UnitsInStock <= ReorderLevel)
            RaiseDomainEvent(new StockBelowReorderLevel(Id, Name, UnitsInStock, ReorderLevel));
    }

    /// <summary>İptal edilen siparişin ürünlerini stoğa geri koyar.</summary>
    public void ReleaseStock(int quantity) => UnitsInStock += Check.Positive(quantity, "Miktar");

    public void Discontinue()
    {
        if (IsDiscontinued)
            return;

        IsDiscontinued = true;
        RaiseDomainEvent(new ProductDiscontinued(Id, Name));
    }
}
