using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Northwind.Domain.Catalog;

namespace Northwind.Infrastructure.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");
        builder.Property(c => c.Name).HasMaxLength(Category.NameMaxLength).IsRequired();
        builder.Property(c => c.Description).HasMaxLength(Category.DescriptionMaxLength);
        builder.HasIndex(c => new { c.TenantId, c.Name });
    }
}

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("Suppliers");
        builder.Property(s => s.CompanyName).HasMaxLength(Supplier.CompanyNameMaxLength).IsRequired();
        builder.Property(s => s.ContactName).HasMaxLength(Supplier.ContactMaxLength);
        builder.Property(s => s.ContactTitle).HasMaxLength(Supplier.ContactMaxLength);
        builder.Property(s => s.Phone).HasMaxLength(Supplier.PhoneMaxLength);
        builder.Property(s => s.Fax).HasMaxLength(Supplier.PhoneMaxLength);
        builder.Property(s => s.HomePage).HasMaxLength(Supplier.HomePageMaxLength);
        builder.OwnsOne(s => s.Address, a => AddressMapping.Configure(a, "Address"));
        builder.HasIndex(s => new { s.TenantId, s.CompanyName });
    }
}

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products");
        builder.Property(p => p.Name).HasMaxLength(Product.NameMaxLength).IsRequired();
        builder.Property(p => p.QuantityPerUnit).HasMaxLength(Product.QuantityPerUnitMaxLength);
        builder.Property(p => p.UnitPrice).HasPrecision(18, 2);
        builder.HasIndex(p => new { p.TenantId, p.Name });

        // Aggregate'ler arası ilişki yalnızca kimlikle; navigation yok.
        builder.HasOne<Category>().WithMany().HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Supplier>().WithMany().HasForeignKey(p => p.SupplierId).OnDelete(DeleteBehavior.Restrict);
    }
}
