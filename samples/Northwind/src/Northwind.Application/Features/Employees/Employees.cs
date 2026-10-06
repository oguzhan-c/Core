using Can.Core.Domain.Results;
using Can.Core.Application;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Northwind.Domain.Employees;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Employees;

public sealed record EmployeeDto(
    Guid Id,
    string FirstName,
    string LastName,
    string? Title,
    DateOnly? HireDate,
    string? City,
    string? Country,
    Guid? ManagerId,
    string? ManagerName,
    IReadOnlyList<string> Territories);

public sealed record GetEmployeeListQuery : IRequest<Result<IReadOnlyList<EmployeeDto>>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

public sealed class GetEmployeeListQueryHandler : IRequestHandler<GetEmployeeListQuery, Result<IReadOnlyList<EmployeeDto>>>
{
    private readonly IRepository<Employee, Guid> _employees;
    private readonly IRepository<Territory, string> _territories;

    public GetEmployeeListQueryHandler(IRepository<Employee, Guid> employees, IRepository<Territory, string> territories)
    {
        _employees = employees;
        _territories = territories;
    }

    public async Task<Result<IReadOnlyList<EmployeeDto>>> Handle(GetEmployeeListQuery request, CancellationToken cancellationToken)
    {
        IQueryable<Employee> employees = _employees.Query(enableTracking: false);
        IQueryable<Territory> territories = _territories.Query(enableTracking: false);

        List<EmployeeDto> list = await employees
            .OrderBy(e => e.LastName)
            .ThenBy(e => e.FirstName)
            .Select(e => new EmployeeDto(
                e.Id,
                e.FirstName,
                e.LastName,
                e.Title,
                e.HireDate,
                e.Address == null ? null : e.Address.City,
                e.Address == null ? null : e.Address.Country,
                e.ManagerId,
                employees.Where(m => m.Id == e.ManagerId).Select(m => m.FirstName + " " + m.LastName).FirstOrDefault(),
                territories
                    .Where(t => e.Territories.Any(et => et.TerritoryCode == t.Id))
                    .OrderBy(t => t.Description)
                    .Select(t => t.Description)
                    .ToList()
            ))
            .ToListAsync(cancellationToken);

        return Result.Ok<IReadOnlyList<EmployeeDto>>(list);
    }
}
