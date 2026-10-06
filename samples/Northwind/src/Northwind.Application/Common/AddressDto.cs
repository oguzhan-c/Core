using Can.Core.Domain.Results;
using Northwind.Domain.Common;

namespace Northwind.Application.Common;

public sealed record AddressDto(string Street, string City, string? Region, string? PostalCode, string Country)
{
    public Result<Address> ToAddress() => Address.Create(Street, City, Region, PostalCode, Country);

    /// <summary>Adres verilmediyse <see langword="null"/> (başarılı), verildiyse doğrulanmış adres.</summary>
    public static Result<Address?> ToOptionalAddress(AddressDto? dto) =>
        dto is null ? Result.Ok<Address?>(null) : dto.ToAddress().Map(a => (Address?)a);
}
