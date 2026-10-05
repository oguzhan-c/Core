using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Northwind.Domain.Catalog;
using Northwind.Domain.Customers;
using Northwind.Domain.Employees;
using Northwind.Domain.Orders;
using Northwind.Domain.Shipping;

namespace Northwind.Infrastructure.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");
        builder.Property(c => c.Code).HasMaxLength(Customer.CodeLength).IsRequired();
        builder.Property(c => c.CompanyName).HasMaxLength(Customer.CompanyNameMaxLength).IsRequired();
        builder.Property(c => c.ContactName).HasMaxLength(Customer.ContactMaxLength);
        builder.Property(c => c.ContactTitle).HasMaxLength(Customer.ContactMaxLength);
        builder.Property(c => c.Phone).HasMaxLength(Customer.PhoneMaxLength);
        builder.Property(c => c.Fax).HasMaxLength(Customer.PhoneMaxLength);
        builder.OwnsOne(c => c.Address, a => AddressMapping.Configure(a, "Address"));

        // Müşteri kodu mağaza içinde benzersiz (silinmişler dahil).
        builder.HasIndex(c => new { c.TenantId, c.Code }).IsUnique();

        // Siteden kayıt olan müşterinin kullanıcı hesabı (bir hesap tek müşteri).
        builder.HasIndex(c => new { c.TenantId, c.UserId }).IsUnique();
        builder.HasOne<Northwind.Domain.Identity.AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ShipperConfiguration : IEntityTypeConfiguration<Shipper>
{
    public void Configure(EntityTypeBuilder<Shipper> builder)
    {
        builder.ToTable("Shippers");
        builder.Property(s => s.CompanyName).HasMaxLength(Shipper.CompanyNameMaxLength).IsRequired();
        builder.Property(s => s.Phone).HasMaxLength(Shipper.PhoneMaxLength);
    }
}

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("Orders");
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(o => o.Freight).HasPrecision(18, 2);
        builder.Property(o => o.ShipName).HasMaxLength(Order.ShipNameMaxLength).IsRequired();
        builder.OwnsOne(o => o.ShipAddress, a => AddressMapping.Configure(a, "Ship"));
        builder.Navigation(o => o.ShipAddress).IsRequired();

        // Aynı anda verilen iki sipariş aynı numarayı alamaz (ikincisi 409 döner).
        builder.HasIndex(o => new { o.TenantId, o.Number }).IsUnique();
        builder.HasIndex(o => new { o.TenantId, o.CustomerId });
        builder.HasIndex(o => new { o.TenantId, o.OrderedAt });

        builder.HasOne<Customer>().WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Employee>().WithMany().HasForeignKey(o => o.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Shipper>().WithMany().HasForeignKey(o => o.ShipperId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(o => o.Lines).WithOne().HasForeignKey(l => l.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        builder.ToTable("OrderLines");
        builder.Property(l => l.ProductName).HasMaxLength(Product.NameMaxLength).IsRequired();
        builder.Property(l => l.UnitPrice).HasPrecision(18, 2);
        builder.Property(l => l.Discount).HasPrecision(5, 4);
        builder.HasIndex(l => l.ProductId);
        builder.HasOne<Product>().WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.ToTable("Employees");
        builder.Property(e => e.FirstName).HasMaxLength(Employee.NameMaxLength).IsRequired();
        builder.Property(e => e.LastName).HasMaxLength(Employee.NameMaxLength).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(Employee.TitleMaxLength);
        builder.Property(e => e.TitleOfCourtesy).HasMaxLength(Employee.TitleMaxLength);
        builder.Property(e => e.HomePhone).HasMaxLength(Employee.PhoneMaxLength);
        builder.Property(e => e.Extension).HasMaxLength(8);
        builder.OwnsOne(e => e.Address, a => AddressMapping.Configure(a, "Address"));

        builder.HasOne<Employee>().WithMany().HasForeignKey(e => e.ManagerId).OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(e => e.Territories).WithOne().HasForeignKey(t => t.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(e => e.Territories).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class EmployeeTerritoryConfiguration : IEntityTypeConfiguration<EmployeeTerritory>
{
    public void Configure(EntityTypeBuilder<EmployeeTerritory> builder)
    {
        builder.ToTable("EmployeeTerritories");
        builder.HasKey(t => new { t.EmployeeId, t.TerritoryCode });
        builder.Property(t => t.TerritoryCode).HasMaxLength(20);
        builder.HasOne<Territory>().WithMany().HasForeignKey(t => t.TerritoryCode).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Bölge ve satış alanları tüm mağazaların ortak kullandığı referans verisidir.</summary>
internal sealed class RegionConfiguration : IEntityTypeConfiguration<Region>
{
    public void Configure(EntityTypeBuilder<Region> builder)
    {
        builder.ToTable("Regions");
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Description).HasMaxLength(60).IsRequired();
    }
}

internal sealed class TerritoryConfiguration : IEntityTypeConfiguration<Territory>
{
    public void Configure(EntityTypeBuilder<Territory> builder)
    {
        builder.ToTable("Territories");
        builder.Property(t => t.Id).HasMaxLength(20).ValueGeneratedNever();
        builder.Property(t => t.Description).HasMaxLength(60).IsRequired();
        builder.HasOne<Region>().WithMany().HasForeignKey(t => t.RegionId).OnDelete(DeleteBehavior.Restrict);
    }
}
