using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Northwind.Domain.Identity;

namespace Northwind.Infrastructure.Persistence.Configurations;

/// <summary>Core'un kullanıcı yapılandırmasına mağazaya özgü kısımları ekler.</summary>
internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.Property(u => u.FirstName).HasMaxLength(AppUser.NameMaxLength).IsRequired();
        builder.Property(u => u.LastName).HasMaxLength(AppUser.NameMaxLength).IsRequired();

        // Aynı e-posta farklı mağazalarda kullanılabilir: benzersizlik mağaza içinde.
        IMutableIndex? globalEmailIndex = builder.Metadata
            .GetIndexes()
            .FirstOrDefault(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(AppUser.NormalizedEmail));

        if (globalEmailIndex is not null)
            builder.Metadata.RemoveIndex(globalEmailIndex);

        builder.HasIndex(u => new { u.TenantId, u.NormalizedEmail }).IsUnique();
    }
}
