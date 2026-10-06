using Can.Core.Domain.Results;
using Can.Core.Persistence.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Northwind.Domain.Catalog;
using Northwind.Domain.Common;
using Northwind.Domain.Customers;
using Northwind.Domain.Employees;
using Northwind.Domain.Orders;
using DomainOrder = Northwind.Domain.Orders.Order;
using Northwind.Domain.Shipping;
using Northwind.Infrastructure.Persistence;

namespace Northwind.Infrastructure.Seeding;

/// <summary>
/// Northwind örnek verisini (<c>northwind.sql</c>) seçilen mağazalara aktarır. Kaynaktaki sayısal kimlikler
/// Guid'lere çevrilir; ilişkiler bu eşleştirmeyle kurulur. Mağazada kategori varsa hiçbir şey yapmaz.
/// </summary>
internal sealed partial class NorthwindDataSeeder : IDataSeeder
{
    private readonly NorthwindDbContext _db;
    private readonly SeedOptions _options;
    private readonly ILogger<NorthwindDataSeeder> _logger;

    public NorthwindDataSeeder(NorthwindDbContext db, SeedOptions options, ILogger<NorthwindDataSeeder> logger)
    {
        _db = db;
        _options = options;
        _logger = logger;
    }

    public int Order => 20;

    public async Task SeedAsync(DataSeedContext context, CancellationToken cancellationToken)
    {
        if (context.Tenant is not { } tenant || !_options.NorthwindTenants.Contains(tenant.Identifier, StringComparer.OrdinalIgnoreCase))
            return;

        // Silinmişler dahil: veri bir kez aktarılır.
        if (await _db.Categories.IgnoreQueryFilters([Can.Core.Persistence.Context.CanQueryFilters.SoftDelete]).AnyAsync(cancellationToken))
            return;

        NorthwindSqlReader sql = NorthwindSqlReader.Load();

        Dictionary<int, Guid> categories = ImportCategories(sql);
        Dictionary<int, Guid> suppliers = ImportSuppliers(sql);
        Dictionary<int, Guid> shippers = ImportShippers(sql);
        Dictionary<int, Guid> employees = ImportEmployees(sql);
        Dictionary<string, Guid> customers = ImportCustomers(sql);
        Dictionary<int, Product> products = ImportProducts(sql, categories, suppliers);
        await _db.SaveChangesAsync(cancellationToken);

        int orderCount = ImportOrders(sql, customers, employees, shippers, products);
        await _db.SaveChangesAsync(cancellationToken);

        LogImported(tenant.Identifier, products.Count, customers.Count, orderCount);
    }

    private Dictionary<int, Guid> ImportCategories(NorthwindSqlReader sql)
    {
        var map = new Dictionary<int, Guid>();
        foreach (NorthwindSqlReader.Row row in sql.Rows("categories"))
        {
            Category category = Category.Create(row.RequiredText(1), row.Text(2)).ThrowIfFailure();
            _db.Categories.Add(category);
            map[row.Int(0)] = category.Id;
        }

        return map;
    }

    private Dictionary<int, Guid> ImportSuppliers(NorthwindSqlReader sql)
    {
        var map = new Dictionary<int, Guid>();
        foreach (NorthwindSqlReader.Row row in sql.Rows("suppliers"))
        {
            // supplier_id, company_name, contact_name, contact_title, address, city, region, postal_code, country, phone, fax, homepage
            Supplier supplier = Supplier.Create(
                row.RequiredText(1),
                row.Text(2),
                row.Text(3),
                AddressOf(row, 4),
                row.Text(9),
                row.Text(10),
                row.Text(11)
            ).ThrowIfFailure();
            _db.Suppliers.Add(supplier);
            map[row.Int(0)] = supplier.Id;
        }

        return map;
    }

    private Dictionary<int, Guid> ImportShippers(NorthwindSqlReader sql)
    {
        var map = new Dictionary<int, Guid>();
        foreach (NorthwindSqlReader.Row row in sql.Rows("shippers"))
        {
            Shipper shipper = Shipper.Create(row.RequiredText(1), row.Text(2)).ThrowIfFailure();
            _db.Shippers.Add(shipper);
            map[row.Int(0)] = shipper.Id;
        }

        return map;
    }

    private Dictionary<int, Guid> ImportEmployees(NorthwindSqlReader sql)
    {
        // employee_id, last_name, first_name, title, title_of_courtesy, birth_date, hire_date, address, city, region,
        // postal_code, country, home_phone, extension, photo, notes, reports_to, photo_path
        var employees = new Dictionary<int, Employee>();
        var managers = new Dictionary<int, int>();

        foreach (NorthwindSqlReader.Row row in sql.Rows("employees"))
        {
            Employee employee = Employee.Create(
                row.RequiredText(2),
                row.RequiredText(1),
                row.Text(3),
                row.Text(4),
                row.Date(5),
                row.Date(6),
                AddressOf(row, 7),
                row.Text(12),
                row.Text(13),
                row.Text(15)
            ).ThrowIfFailure();

            employees[row.Int(0)] = employee;
            if (row.NullableInt(16) is { } managerId)
                managers[row.Int(0)] = managerId;

            _db.Employees.Add(employee);
        }

        foreach ((int employeeId, int managerId) in managers)
            employees[employeeId].ReportTo(employees[managerId]).ThrowIfFailure();

        foreach (NorthwindSqlReader.Row row in sql.Rows("employee_territories"))
            employees[row.Int(0)].AssignTerritory(row.RequiredText(1));

        return employees.ToDictionary(e => e.Key, e => e.Value.Id);
    }

    private Dictionary<string, Guid> ImportCustomers(NorthwindSqlReader sql)
    {
        // customer_id, company_name, contact_name, contact_title, address, city, region, postal_code, country, phone, fax
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (NorthwindSqlReader.Row row in sql.Rows("customers"))
        {
            Customer customer = Customer.Create(
                row.RequiredText(0),
                row.RequiredText(1),
                row.Text(2),
                row.Text(3),
                AddressOf(row, 4),
                row.Text(9),
                row.Text(10)
            ).ThrowIfFailure();
            _db.Customers.Add(customer);
            map[row.RequiredText(0)] = customer.Id;
        }

        return map;
    }

    private Dictionary<int, Product> ImportProducts(NorthwindSqlReader sql, Dictionary<int, Guid> categories, Dictionary<int, Guid> suppliers)
    {
        // product_id, product_name, supplier_id, category_id, quantity_per_unit, unit_price, units_in_stock,
        // units_on_order, reorder_level, discontinued
        var map = new Dictionary<int, Product>();
        foreach (NorthwindSqlReader.Row row in sql.Rows("products"))
        {
            Product product = Product.Import(
                row.RequiredText(1),
                row.NullableInt(3) is { } c ? categories[c] : null,
                row.NullableInt(2) is { } s ? suppliers[s] : null,
                row.Text(4),
                row.Decimal(5),
                row.NullableInt(6) ?? 0,
                row.NullableInt(7) ?? 0,
                row.NullableInt(8) ?? 0,
                row.Int(9) == 1
            ).ThrowIfFailure();
            _db.Products.Add(product);
            map[row.Int(0)] = product;
        }

        return map;
    }

    private int ImportOrders(
        NorthwindSqlReader sql,
        Dictionary<string, Guid> customers,
        Dictionary<int, Guid> employees,
        Dictionary<int, Guid> shippers,
        Dictionary<int, Product> products)
    {
        // order_id, product_id, unit_price, quantity, discount
        ILookup<int, NorthwindSqlReader.Row> details = sql.Rows("order_details").ToLookup(r => r.Int(0));
        int count = 0;

        // order_id, customer_id, employee_id, order_date, required_date, shipped_date, ship_via, freight,
        // ship_name, ship_address, ship_city, ship_region, ship_postal_code, ship_country
        foreach (NorthwindSqlReader.Row row in sql.Rows("orders"))
        {
            int number = row.Int(0);
            Address shipAddress =
                AddressOf(row, 9) ?? throw new InvalidDataException($"#{number} siparişinin teslimat adresi eksik.");

            // "Order" bu sınıfta IDataSeeder.Order (çalışma sırası) özelliğini gösterir; sınıfı takma adla kullan.
            DomainOrder order = DomainOrder.Import(
                number,
                customers[row.RequiredText(1)],
                row.NullableInt(2) is { } e ? employees[e] : null,
                AtMidnightUtc(row.Date(3)!.Value),
                row.Date(4),
                row.Date(5) is { } shipped ? AtMidnightUtc(shipped) : null,
                row.NullableInt(6) is { } s ? shippers[s] : null,
                row.Decimal(7),
                row.RequiredText(8),
                shipAddress,
                details[number].Select(d =>
                {
                    Product product = products[d.Int(1)];
                    return (product.Id, product.Name, d.Decimal(2), d.Int(3), d.Decimal(4));
                })
            );

            _db.Orders.Add(order);
            count++;
        }

        return count;
    }

    /// <summary>Kaynakta adres, şehir, bölge, posta kodu, ülke ardışık sütunlardır.</summary>
    private static Address? AddressOf(NorthwindSqlReader.Row row, int streetIndex)
    {
        string? street = row.Text(streetIndex);
        string? city = row.Text(streetIndex + 1);
        string? country = row.Text(streetIndex + 4);

        return street is null || city is null || country is null
            ? null
            : Address.Create(street, city, row.Text(streetIndex + 2), row.Text(streetIndex + 3), country).ThrowIfFailure();
    }

    private static DateTimeOffset AtMidnightUtc(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    [LoggerMessage(Level = LogLevel.Information, Message = "Northwind verisi '{Tenant}' mağazasına aktarıldı: {Products} ürün, {Customers} müşteri, {Orders} sipariş.")]
    private partial void LogImported(string tenant, int products, int customers, int orders);
}
