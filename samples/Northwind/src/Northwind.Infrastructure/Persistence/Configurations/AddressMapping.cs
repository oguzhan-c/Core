using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Northwind.Domain.Common;

namespace Northwind.Infrastructure.Persistence.Configurations;

internal static class AddressMapping
{
    /// <summary>Adresi sahibinin tablosunda, ön ekli sütunlar olarak saklar (ör. <c>AddressCity</c>).</summary>
    public static void Configure<TOwner>(OwnedNavigationBuilder<TOwner, Address> address, string prefix)
        where TOwner : class
    {
        address.Property(a => a.Street).HasColumnName($"{prefix}Street").HasMaxLength(Address.StreetMaxLength);
        address.Property(a => a.City).HasColumnName($"{prefix}City").HasMaxLength(Address.CityMaxLength);
        address.Property(a => a.Region).HasColumnName($"{prefix}Region").HasMaxLength(Address.RegionMaxLength);
        address.Property(a => a.PostalCode).HasColumnName($"{prefix}PostalCode").HasMaxLength(Address.PostalCodeMaxLength);
        address.Property(a => a.Country).HasColumnName($"{prefix}Country").HasMaxLength(Address.CountryMaxLength);
    }
}
