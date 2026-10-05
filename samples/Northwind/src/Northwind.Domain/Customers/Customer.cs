using Can.Core.Domain.Auditing;
using Northwind.Domain.Common;

namespace Northwind.Domain.Customers;

[Audited]
public sealed class Customer : TenantAggregateRoot
{
    public const int CodeLength = 5;
    public const int CompanyNameMaxLength = 60;
    public const int ContactMaxLength = 40;
    public const int PhoneMaxLength = 24;

    private Customer()
    {
        Code = string.Empty;
        CompanyName = string.Empty;
    }

    private Customer(Guid id, string code)
        : base(id)
    {
        Code = NormalizeCode(code);
        CompanyName = string.Empty;
    }

    /// <summary>Kısa müşteri kodu (ör. <c>ALFKI</c>); mağaza içinde benzersiz.</summary>
    public string Code { get; private set; }

    public string CompanyName { get; private set; }

    public string? ContactName { get; private set; }

    public string? ContactTitle { get; private set; }

    public Address? Address { get; private set; }

    public string? Phone { get; private set; }

    public string? Fax { get; private set; }

    /// <summary>Siteden kayıt olduysa müşterinin kullanıcı hesabı.</summary>
    public Guid? UserId { get; private set; }

    public static Customer Create(
        string code,
        string companyName,
        string? contactName,
        string? contactTitle,
        Address? address,
        string? phone,
        string? fax)
    {
        var customer = new Customer(Guid.CreateVersion7(), code);
        customer.Update(companyName, contactName, contactTitle, address, phone, fax);
        return customer;
    }

    public void Update(string companyName, string? contactName, string? contactTitle, Address? address, string? phone, string? fax)
    {
        CompanyName = Check.Required(companyName, "Firma adı", CompanyNameMaxLength);
        ContactName = Check.Optional(contactName, "Yetkili", ContactMaxLength);
        ContactTitle = Check.Optional(contactTitle, "Yetkili unvanı", ContactMaxLength);
        Address = address;
        Phone = Check.Optional(phone, "Telefon", PhoneMaxLength);
        Fax = Check.Optional(fax, "Faks", PhoneMaxLength);
    }

    /// <summary>Müşteriyi bir kullanıcı hesabına bağlar (site üzerinden sipariş verebilmesi için).</summary>
    public void LinkUser(Guid userId)
    {
        if (UserId is { } current && current != userId)
            throw new Can.Core.Domain.Exceptions.BusinessException("Müşteri zaten başka bir hesaba bağlı.");

        UserId = userId;
    }

    public static string NormalizeCode(string code)
    {
        string normalized = Check.Required(code, "Müşteri kodu", CodeLength).ToUpperInvariant();
        return normalized.All(char.IsLetterOrDigit)
            ? normalized
            : throw new Can.Core.Domain.Exceptions.BusinessException("Müşteri kodu yalnızca harf ve rakam içerebilir.");
    }
}
