using Can.Core.Domain.ValueObjects;

namespace Northwind.Domain.Common;

/// <summary>Posta adresi (value object). Müşteri, tedarikçi, çalışan ve sipariş teslimat adresinde kullanılır.</summary>
public sealed class Address : ValueObject
{
    public const int StreetMaxLength = 60;
    public const int CityMaxLength = 30;
    public const int RegionMaxLength = 30;
    public const int PostalCodeMaxLength = 10;
    public const int CountryMaxLength = 30;

    private Address()
    {
        Street = string.Empty;
        City = string.Empty;
        Country = string.Empty;
    }

    public Address(string street, string city, string? region, string? postalCode, string country)
    {
        Street = Check.Required(street, "Adres", StreetMaxLength);
        City = Check.Required(city, "Şehir", CityMaxLength);
        Region = Check.Optional(region, "Bölge", RegionMaxLength);
        PostalCode = Check.Optional(postalCode, "Posta kodu", PostalCodeMaxLength);
        Country = Check.Required(country, "Ülke", CountryMaxLength);
    }

    public string Street { get; private set; }

    public string City { get; private set; }

    public string? Region { get; private set; }

    public string? PostalCode { get; private set; }

    public string Country { get; private set; }

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Street;
        yield return City;
        yield return Region;
        yield return PostalCode;
        yield return Country;
    }

    public override string ToString() =>
        string.Join(", ", new[] { Street, PostalCode is null ? City : $"{PostalCode} {City}", Region, Country }.Where(p => !string.IsNullOrEmpty(p)));
}
