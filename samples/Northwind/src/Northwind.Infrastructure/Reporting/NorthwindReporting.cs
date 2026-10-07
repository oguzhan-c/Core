using Can.Core.Reporting;
using Can.Core.Reporting.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Northwind.Domain.Identity;
using Northwind.Domain.Orders;
using Northwind.Infrastructure.Persistence;

namespace Northwind.Infrastructure.Reporting;

/// <summary>Satış raporlarının satırı: sipariş satırı + sipariş, müşteri, ürün, kategori, çalışan, kargo bilgisi.</summary>
public sealed class SalesReportRow
{
    public int OrderNumber { get; init; }

    public DateTime OrderDate { get; init; }

    public DateTime? ShippedDate { get; init; }

    public string Status { get; init; } = string.Empty;

    public string Customer { get; init; } = string.Empty;

    public string ShipCountry { get; init; } = string.Empty;

    public string ShipCity { get; init; } = string.Empty;

    public string? Employee { get; init; }

    public string? Shipper { get; init; }

    public string? Category { get; init; }

    public string Product { get; init; } = string.Empty;

    public decimal UnitPrice { get; init; }

    public int Quantity { get; init; }

    public decimal Discount { get; init; }

    public decimal LineTotal { get; init; }
}

/// <summary>Stok raporlarının satırı: ürün + kategori + tedarikçi.</summary>
public sealed class InventoryReportRow
{
    public string Product { get; init; } = string.Empty;

    public string? Category { get; init; }

    public string? Supplier { get; init; }

    public string? SupplierCountry { get; init; }

    public decimal UnitPrice { get; init; }

    public int UnitsInStock { get; init; }

    public int UnitsOnOrder { get; init; }

    public int ReorderLevel { get; init; }

    public decimal StockValue { get; init; }

    public bool NeedsReorder { get; init; }

    public bool IsDiscontinued { get; init; }
}

public static class NorthwindReporting
{
    /// <summary>
    /// Rapor veri kaynakları (<c>sales</c>, <c>inventory</c>) ve kaydedilmiş raporların EF deposu. Sorgular DbContext
    /// üzerinden gittiği için tenant filtresi kendiliğinden uygulanır; gruplama mümkünse veritabanında (GROUP BY) yapılır.
    /// </summary>
    public static IServiceCollection AddNorthwindReporting(this IServiceCollection services)
    {
        services.AddCanReporting()
            .AddSource("sales", sp => SalesRows(sp.GetRequiredService<NorthwindDbContext>()), o =>
            {
                o.Caption = "Satışlar";
                o.Permission = Permissions.ReportsSales;
                o.Field("orderNumber", "Sipariş no")
                    .Field("orderDate", "Sipariş tarihi", "d")
                    .Field("shippedDate", "Kargo tarihi", "d")
                    .Field("status", "Durum")
                    .Field("customer", "Müşteri")
                    .Field("shipCountry", "Ülke")
                    .Field("shipCity", "Şehir")
                    .Field("employee", "Çalışan")
                    .Field("shipper", "Kargo firması")
                    .Field("category", "Kategori")
                    .Field("product", "Ürün")
                    .Field("unitPrice", "Birim fiyat", "N2")
                    .Field("quantity", "Adet", "N0")
                    .Field("discount", "İndirim", "P0")
                    .Field("lineTotal", "Tutar", "N2");
            })
            .AddSource("inventory", sp => InventoryRows(sp.GetRequiredService<NorthwindDbContext>()), o =>
            {
                o.Caption = "Stok";
                o.Permission = Permissions.ReportsInventory;
                o.Field("product", "Ürün")
                    .Field("category", "Kategori")
                    .Field("supplier", "Tedarikçi")
                    .Field("supplierCountry", "Tedarikçi ülkesi")
                    .Field("unitPrice", "Birim fiyat", "N2")
                    .Field("unitsInStock", "Stok", "N0")
                    .Field("unitsOnOrder", "Siparişte", "N0")
                    .Field("reorderLevel", "Yeniden sipariş seviyesi", "N0")
                    .Field("stockValue", "Stok değeri", "N2")
                    .Field("needsReorder", "Sipariş gerekli")
                    .Field("isDiscontinued", "Satıştan kalktı");
            })
            .UseEntityFrameworkStore<NorthwindDbContext>();

        return services;
    }

    internal static IQueryable<SalesReportRow> SalesRows(NorthwindDbContext db) =>
        from o in db.Orders.AsNoTracking()
        from l in o.Lines
        join p in db.Products on l.ProductId equals p.Id
        join c in db.Categories on p.CategoryId equals (Guid?)c.Id into categories
        from c in categories.DefaultIfEmpty()
        join cu in db.Customers on o.CustomerId equals cu.Id
        join e in db.Employees on o.EmployeeId equals (Guid?)e.Id into employees
        from e in employees.DefaultIfEmpty()
        join s in db.Shippers on o.ShipperId equals (Guid?)s.Id into shippers
        from s in shippers.DefaultIfEmpty()
        select new SalesReportRow
        {
            OrderNumber = o.Number,
            OrderDate = o.OrderedAt.UtcDateTime,
            ShippedDate = o.ShippedAt == null ? null : o.ShippedAt!.Value.UtcDateTime,
            Status = o.Status == OrderStatus.Placed ? "Yeni" : o.Status == OrderStatus.Shipped ? "Kargoda" : "İptal",
            Customer = cu.CompanyName,
            ShipCountry = o.ShipAddress.Country,
            ShipCity = o.ShipAddress.City,
            Employee = e == null ? null : e.FirstName + " " + e.LastName,
            Shipper = s == null ? null : s.CompanyName,
            Category = c == null ? null : c.Name,
            Product = l.ProductName,
            UnitPrice = l.UnitPrice,
            Quantity = l.Quantity,
            Discount = l.Discount,
            LineTotal = l.UnitPrice * l.Quantity * (1 - l.Discount),
        };

    internal static IQueryable<InventoryReportRow> InventoryRows(NorthwindDbContext db) =>
        from p in db.Products.AsNoTracking()
        join c in db.Categories on p.CategoryId equals (Guid?)c.Id into categories
        from c in categories.DefaultIfEmpty()
        join s in db.Suppliers on p.SupplierId equals (Guid?)s.Id into suppliers
        from s in suppliers.DefaultIfEmpty()
        select new InventoryReportRow
        {
            Product = p.Name,
            Category = c == null ? null : c.Name,
            Supplier = s == null ? null : s.CompanyName,
            SupplierCountry = s == null || s.Address == null ? null : s.Address.Country,
            UnitPrice = p.UnitPrice,
            UnitsInStock = p.UnitsInStock,
            UnitsOnOrder = p.UnitsOnOrder,
            ReorderLevel = p.ReorderLevel,
            StockValue = p.UnitPrice * p.UnitsInStock,
            NeedsReorder = p.UnitsInStock + p.UnitsOnOrder <= p.ReorderLevel,
            IsDiscontinued = p.IsDiscontinued,
        };
}
