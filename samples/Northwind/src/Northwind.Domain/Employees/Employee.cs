using Can.Core.Domain.Results;
using Northwind.Domain.Common;

namespace Northwind.Domain.Employees;

public sealed class Employee : TenantAggregateRoot
{
    public const int NameMaxLength = 30;
    public const int TitleMaxLength = 40;
    public const int PhoneMaxLength = 24;

    private readonly List<EmployeeTerritory> _territories = [];

    private Employee()
    {
        FirstName = string.Empty;
        LastName = string.Empty;
    }

    private Employee(Guid id)
        : base(id)
    {
        FirstName = string.Empty;
        LastName = string.Empty;
    }

    public string FirstName { get; private set; }

    public string LastName { get; private set; }

    public string FullName => $"{FirstName} {LastName}";

    public string? Title { get; private set; }

    /// <summary>Hitap, ör. "Ms.", "Dr.".</summary>
    public string? TitleOfCourtesy { get; private set; }

    public DateOnly? BirthDate { get; private set; }

    public DateOnly? HireDate { get; private set; }

    public Address? Address { get; private set; }

    public string? HomePhone { get; private set; }

    public string? Extension { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>Bağlı olduğu yönetici.</summary>
    public Guid? ManagerId { get; private set; }

    public IReadOnlyCollection<EmployeeTerritory> Territories => _territories;

    public static Result<Employee> Create(
        string firstName,
        string lastName,
        string? title,
        string? titleOfCourtesy,
        DateOnly? birthDate,
        DateOnly? hireDate,
        Address? address,
        string? homePhone,
        string? extension,
        string? notes)
    {
        Result<Success> valid = Result.Validate(
            Check.Required(firstName, "Ad", NameMaxLength),
            Check.Required(lastName, "Soyad", NameMaxLength),
            Check.Optional(title, "Unvan", TitleMaxLength),
            Check.Optional(titleOfCourtesy, "Hitap", TitleMaxLength),
            Check.Optional(homePhone, "Telefon", PhoneMaxLength),
            Check.Optional(extension, "Dahili", 8)
        );
        if (valid.IsFailure)
            return valid.Errors;

        return new Employee(Guid.CreateVersion7())
        {
            FirstName = Check.Clean(firstName),
            LastName = Check.Clean(lastName),
            Title = Check.CleanOptional(title),
            TitleOfCourtesy = Check.CleanOptional(titleOfCourtesy),
            BirthDate = birthDate,
            HireDate = hireDate,
            Address = address,
            HomePhone = Check.CleanOptional(homePhone),
            Extension = Check.CleanOptional(extension),
            Notes = notes,
        };
    }

    public Result<Success> ReportTo(Employee? manager)
    {
        if (manager is not null && manager.Id == Id)
            return Error.Failure("employee.self_manager", "Çalışan kendisine bağlı olamaz.");

        ManagerId = manager?.Id;
        return Result.Success;
    }

    public void AssignTerritory(string territoryCode)
    {
        if (_territories.Any(t => t.TerritoryCode == territoryCode))
            return;

        _territories.Add(new EmployeeTerritory(Id, territoryCode));
    }
}

/// <summary>Çalışanın sorumlu olduğu satış alanı.</summary>
public sealed class EmployeeTerritory
{
    private EmployeeTerritory()
    {
        TerritoryCode = string.Empty;
    }

    internal EmployeeTerritory(Guid employeeId, string territoryCode)
    {
        EmployeeId = employeeId;
        TerritoryCode = territoryCode;
    }

    public Guid EmployeeId { get; private set; }

    public string TerritoryCode { get; private set; }
}
