using Can.Core.Domain.Results;
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
        Code = code;
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

    public static Result<Customer> Create(
        string code,
        string companyName,
        string? contactName,
        string? contactTitle,
        Address? address,
        string? phone,
        string? fax)
    {
        return NormalizeCode(code).Then(normalized =>
        {
            var customer = new Customer(Guid.CreateVersion7(), normalized);
            return customer.Update(companyName, contactName, contactTitle, address, phone, fax).Map(_ => customer);
        });
    }

    public Result<Success> Update(string companyName, string? contactName, string? contactTitle, Address? address, string? phone, string? fax)
    {
        Result<Success> valid = Result.Validate(
            Check.Required(companyName, "Firma adı", CompanyNameMaxLength),
            Check.Optional(contactName, "Yetkili", ContactMaxLength),
            Check.Optional(contactTitle, "Yetkili unvanı", ContactMaxLength),
            Check.Optional(phone, "Telefon", PhoneMaxLength),
            Check.Optional(fax, "Faks", PhoneMaxLength)
        );
        if (valid.IsFailure)
            return valid;

        CompanyName = Check.Clean(companyName);
        ContactName = Check.CleanOptional(contactName);
        ContactTitle = Check.CleanOptional(contactTitle);
        Address = address;
        Phone = Check.CleanOptional(phone);
        Fax = Check.CleanOptional(fax);
        return Result.Success;
    }

    /// <summary>Müşteriyi bir kullanıcı hesabına bağlar (site üzerinden sipariş verebilmesi için).</summary>
    public Result<Success> LinkUser(Guid userId)
    {
        if (UserId is { } current && current != userId)
            return CustomerErrors.AlreadyLinked;

        UserId = userId;
        return Result.Success;
    }

    /// <summary>Müşteri kodu: 1-5 harf/rakam, büyük harfe çevrilir (ör. <c>" alfki"</c> → <c>"ALFKI"</c>).</summary>
    public static Result<string> NormalizeCode(string code)
    {
        if (Check.Required(code, "Müşteri kodu", CodeLength) is { } error)
            return error;

        string normalized = Check.Clean(code).ToUpperInvariant();
        return normalized.All(char.IsLetterOrDigit) ? normalized : CustomerErrors.InvalidCode;
    }
}

/// <summary>Müşteri hataları.</summary>
public static class CustomerErrors
{
    public static readonly Error AlreadyLinked = Error.Conflict("customer.already_linked", "Müşteri zaten başka bir hesaba bağlı.");

    public static readonly Error InvalidCode =
        Error.Validation("customer.invalid_code", "Müşteri kodu yalnızca harf ve rakam içerebilir.", "Code");

    public static Error NotFound(Guid id) => Error.NotFound("customer.not_found", $"'{id}' müşterisi bulunamadı.");
}
