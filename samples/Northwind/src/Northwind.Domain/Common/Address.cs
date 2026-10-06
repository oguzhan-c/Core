using Can.Core.Domain.Results;
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

    private Address(string street, string city, string? region, string? postalCode, string country)
    {
        Street = Check.Clean(street);
        City = Check.Clean(city);
        Region = Check.CleanOptional(region);
        PostalCode = Check.CleanOptional(postalCode);
        Country = Check.Clean(country);
    }

    public static Result<Address> Create(string street, string city, string? region, string? postalCode, string country)
    {
        Result<Success> valid = Result.Validate(
            Check.Required(street, "Adres", StreetMaxLength),
            Check.Required(city, "Şehir", CityMaxLength),
            Check.Optional(region, "Bölge", RegionMaxLength),
            Check.Optional(postalCode, "Posta kodu", PostalCodeMaxLength),
            Check.Required(country, "Ülke", CountryMaxLength)
        );

        return valid.IsFailure ? valid.Errors : new Address(street, city, region, postalCode, country);
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
