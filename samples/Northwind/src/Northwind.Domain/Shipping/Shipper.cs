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

    public static Shipper Create(string companyName, string? phone)
    {
        var shipper = new Shipper(Guid.CreateVersion7());
        shipper.Update(companyName, phone);
        return shipper;
    }

    public void Update(string companyName, string? phone)
    {
        CompanyName = Check.Required(companyName, "Firma adı", CompanyNameMaxLength);
        Phone = Check.Optional(phone, "Telefon", PhoneMaxLength);
    }
}
