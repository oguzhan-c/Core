using Can.Core.Domain.Results;
using Northwind.Domain.Common;

namespace Northwind.Domain.Catalog;

public sealed class Supplier : TenantAggregateRoot
{
    public const int CompanyNameMaxLength = 60;
    public const int ContactMaxLength = 40;
    public const int PhoneMaxLength = 24;
    public const int HomePageMaxLength = 200;

    private Supplier()
    {
        CompanyName = string.Empty;
    }

    private Supplier(Guid id)
        : base(id)
    {
        CompanyName = string.Empty;
    }

    public string CompanyName { get; private set; }

    public string? ContactName { get; private set; }

    public string? ContactTitle { get; private set; }

    public Address? Address { get; private set; }

    public string? Phone { get; private set; }

    public string? Fax { get; private set; }

    public string? HomePage { get; private set; }

    public static Result<Supplier> Create(
        string companyName,
        string? contactName,
        string? contactTitle,
        Address? address,
        string? phone,
        string? fax,
        string? homePage)
    {
        var supplier = new Supplier(Guid.CreateVersion7());
        return supplier.Update(companyName, contactName, contactTitle, address, phone, fax, homePage).Map(_ => supplier);
    }

    public Result<Success> Update(
        string companyName,
        string? contactName,
        string? contactTitle,
        Address? address,
        string? phone,
        string? fax,
        string? homePage)
    {
        Result<Success> valid = Result.Validate(
            Check.Required(companyName, "Firma adı", CompanyNameMaxLength),
            Check.Optional(contactName, "Yetkili", ContactMaxLength),
            Check.Optional(contactTitle, "Yetkili unvanı", ContactMaxLength),
            Check.Optional(phone, "Telefon", PhoneMaxLength),
            Check.Optional(fax, "Faks", PhoneMaxLength),
            Check.Optional(homePage, "Web sitesi", HomePageMaxLength)
        );
        if (valid.IsFailure)
            return valid;

        CompanyName = Check.Clean(companyName);
        ContactName = Check.CleanOptional(contactName);
        ContactTitle = Check.CleanOptional(contactTitle);
        Address = address;
        Phone = Check.CleanOptional(phone);
        Fax = Check.CleanOptional(fax);
        HomePage = Check.CleanOptional(homePage);
        return Result.Success;
    }
}
