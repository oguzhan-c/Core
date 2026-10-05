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

    public static Supplier Create(
        string companyName,
        string? contactName,
        string? contactTitle,
        Address? address,
        string? phone,
        string? fax,
        string? homePage)
    {
        var supplier = new Supplier(Guid.CreateVersion7());
        supplier.Update(companyName, contactName, contactTitle, address, phone, fax, homePage);
        return supplier;
    }

    public void Update(
        string companyName,
        string? contactName,
        string? contactTitle,
        Address? address,
        string? phone,
        string? fax,
        string? homePage)
    {
        CompanyName = Check.Required(companyName, "Firma adı", CompanyNameMaxLength);
        ContactName = Check.Optional(contactName, "Yetkili", ContactMaxLength);
        ContactTitle = Check.Optional(contactTitle, "Yetkili unvanı", ContactMaxLength);
        Address = address;
        Phone = Check.Optional(phone, "Telefon", PhoneMaxLength);
        Fax = Check.Optional(fax, "Faks", PhoneMaxLength);
        HomePage = Check.Optional(homePage, "Web sitesi", HomePageMaxLength);
    }
}
