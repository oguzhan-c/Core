using Northwind.Domain.Common;

namespace Northwind.Application.Common;

public sealed record AddressDto(string Street, string City, string? Region, string? PostalCode, string Country)
{
    public Address ToAddress() => new(Street, City, Region, PostalCode, Country);
}
