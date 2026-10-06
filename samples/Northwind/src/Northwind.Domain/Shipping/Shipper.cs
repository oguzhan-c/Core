using Can.Core.Domain.Results;
using Northwind.Domain.Common;

namespace Northwind.Domain.Shipping;

/// <summary>Kargo firması.</summary>
public sealed class Shipper : TenantAggregateRoot
{
    public const int CompanyNameMaxLength = 60;
    public const int PhoneMaxLength = 24;

    private Shipper()
    {
        CompanyName = string.Empty;
    }

    private Shipper(Guid id)
        : base(id)
    {
        CompanyName = string.Empty;
    }

    public string CompanyName { get; private set; }

    public string? Phone { get; private set; }

    public static Result<Shipper> Create(string companyName, string? phone)
    {
        var shipper = new Shipper(Guid.CreateVersion7());
        return shipper.Update(companyName, phone).Map(_ => shipper);
    }

    public Result<Success> Update(string companyName, string? phone)
    {
        Result<Success> valid = Result.Validate(
            Check.Required(companyName, "Firma adı", CompanyNameMaxLength),
            Check.Optional(phone, "Telefon", PhoneMaxLength)
        );
        if (valid.IsFailure)
            return valid;

        CompanyName = Check.Clean(companyName);
        Phone = Check.CleanOptional(phone);
        return Result.Success;
    }
}
