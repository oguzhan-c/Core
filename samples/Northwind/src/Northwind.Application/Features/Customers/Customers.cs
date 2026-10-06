using Can.Core.Application;
using Can.Core.Application.Rules;
using Can.Core.Domain.Results;
using Can.Core.Mapping;
using Can.Core.Mediator;
using Can.Core.Persistence.Dynamic;
using Can.Core.Persistence.Paging;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Common;
using Northwind.Domain.Common;
using Northwind.Domain.Customers;
using Northwind.Domain.Orders;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Customers;

// ---------------------------------------------------------------- modeller

public sealed record CustomerDto(
    Guid Id,
    string Code,
    string CompanyName,
    string? ContactName,
    string? ContactTitle,
    AddressDto? Address,
    string? Phone,
    string? Fax);

public sealed record CustomerListItemDto(Guid Id, string Code, string CompanyName, string? ContactName, string? City, string? Country, string? Phone);

public sealed class CustomerProfile : MappingProfile
{
    public CustomerProfile()
    {
        CreateMap<Customer, CustomerDto>();
        CreateMap<Customer, CustomerListItemDto>()
            .ForMember(d => d.City, o => o.MapFrom(s => s.Address == null ? null : s.Address.City))
            .ForMember(d => d.Country, o => o.MapFrom(s => s.Address == null ? null : s.Address.Country));
    }
}

// ---------------------------------------------------------------- kurallar

public sealed class CustomerBusinessRules : BaseBusinessRules
{
    private readonly IRepository<Customer, Guid> _customers;
    private readonly IRepository<Order, Guid> _orders;

    public CustomerBusinessRules(IRepository<Customer, Guid> customers, IRepository<Order, Guid> orders)
    {
        _customers = customers;
        _orders = orders;
    }

    public Task<Result<Customer>> MustExistAsync(Guid id, CancellationToken cancellationToken) =>
        _customers.GetByIdAsync(id, cancellationToken: cancellationToken).ToResult(CustomerErrors.NotFound(id));

    /// <summary>Silinmiş müşterilerin kodları da tekrar kullanılamaz (geçmiş siparişlerde görünür).</summary>
    public async Task<Result<Success>> CodeMustBeUniqueAsync(string code, CancellationToken cancellationToken)
    {
        Result<string> normalized = Customer.NormalizeCode(code);
        if (normalized.IsFailure)
            return normalized.Errors;

        string value = normalized.Value;
        return await _customers.AnyAsync(c => c.Code == value, withDeleted: true, cancellationToken: cancellationToken)
            ? Error.Conflict("customer.duplicate_code", $"'{value}' kodlu bir müşteri zaten var.")
            : Result.Success;
    }

    public async Task<Result<Success>> MustHaveNoOpenOrdersAsync(Guid customerId, CancellationToken cancellationToken) =>
        await _orders.AnyAsync(o => o.CustomerId == customerId && o.Status == OrderStatus.Placed, cancellationToken: cancellationToken)
            ? Error.Failure("customer.has_open_orders", "Açık siparişi olan müşteri silinemez.")
            : Result.Success;
}

// ---------------------------------------------------------------- command'lar

public sealed record CreateCustomerCommand(
    string Code,
    string CompanyName,
    string? ContactName,
    string? ContactTitle,
    AddressDto? Address,
    string? Phone,
    string? Fax) : IRequest<Result<Guid>>, ISecuredRequest, ITransactionalRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Sales];
}

public sealed record UpdateCustomerCommand(
    Guid Id,
    string CompanyName,
    string? ContactName,
    string? ContactTitle,
    AddressDto? Address,
    string? Phone,
    string? Fax) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Sales];
}

public sealed record DeleteCustomerCommand(Guid Id) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
}

public sealed class CreateCustomerCommandValidator : AbstractValidator<CreateCustomerCommand>
{
    public CreateCustomerCommandValidator()
    {
        RuleFor(c => c.Code).NotEmpty().Length(1, Customer.CodeLength).Matches("^[A-Za-z0-9]+$");
        RuleFor(c => c.CompanyName).NotEmpty().MaximumLength(Customer.CompanyNameMaxLength);
        RuleFor(c => c.ContactName).MaximumLength(Customer.ContactMaxLength);
        RuleFor(c => c.ContactTitle).MaximumLength(Customer.ContactMaxLength);
        RuleFor(c => c.Phone).MaximumLength(Customer.PhoneMaxLength);
        RuleFor(c => c.Fax).MaximumLength(Customer.PhoneMaxLength);
        RuleFor(c => c.Address).ValidAddress();
    }
}

public sealed class UpdateCustomerCommandValidator : AbstractValidator<UpdateCustomerCommand>
{
    public UpdateCustomerCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.CompanyName).NotEmpty().MaximumLength(Customer.CompanyNameMaxLength);
        RuleFor(c => c.ContactName).MaximumLength(Customer.ContactMaxLength);
        RuleFor(c => c.ContactTitle).MaximumLength(Customer.ContactMaxLength);
        RuleFor(c => c.Phone).MaximumLength(Customer.PhoneMaxLength);
        RuleFor(c => c.Fax).MaximumLength(Customer.PhoneMaxLength);
        RuleFor(c => c.Address).ValidAddress();
    }
}

public sealed class CustomerCommandHandlers
    : IRequestHandler<CreateCustomerCommand, Result<Guid>>,
        IRequestHandler<UpdateCustomerCommand, Result<Success>>,
        IRequestHandler<DeleteCustomerCommand, Result<Success>>
{
    private readonly IRepository<Customer, Guid> _customers;
    private readonly CustomerBusinessRules _rules;

    public CustomerCommandHandlers(IRepository<Customer, Guid> customers, CustomerBusinessRules rules)
    {
        _customers = customers;
        _rules = rules;
    }

    public async Task<Result<Guid>> Handle(CreateCustomerCommand request, CancellationToken cancellationToken)
    {
        Result<Address?> address = AddressDto.ToOptionalAddress(request.Address);
        Result<Success> unique = await _rules.CodeMustBeUniqueAsync(request.Code, cancellationToken);

        Result<Success> checks = Result.Combine(address, unique);
        if (checks.IsFailure)
            return checks.Errors;

        return await Customer
            .Create(request.Code, request.CompanyName, request.ContactName, request.ContactTitle, address.Value, request.Phone, request.Fax)
            .TapAsync(customer => _customers.AddAsync(customer, cancellationToken))
            .Map(customer => customer.Id);
    }

    public async Task<Result<Success>> Handle(UpdateCustomerCommand request, CancellationToken cancellationToken)
    {
        Result<Address?> address = AddressDto.ToOptionalAddress(request.Address);
        if (address.IsFailure)
            return address.Errors;

        return await _rules
            .MustExistAsync(request.Id, cancellationToken)
            .Then(customer =>
                customer.Update(request.CompanyName, request.ContactName, request.ContactTitle, address.Value, request.Phone, request.Fax)
            );
    }

    public async Task<Result<Success>> Handle(DeleteCustomerCommand request, CancellationToken cancellationToken)
    {
        Result<Customer> customer = await _rules.MustExistAsync(request.Id, cancellationToken);
        if (customer.IsFailure)
            return customer.Errors;

        Result<Success> noOpenOrders = await _rules.MustHaveNoOpenOrdersAsync(request.Id, cancellationToken);
        if (noOpenOrders.IsFailure)
            return noOpenOrders;

        _customers.Delete(customer.Value);
        return Result.Success;
    }
}

// ---------------------------------------------------------------- query'ler

public sealed record GetCustomerListQuery(PageRequest Page, string? Search = null, string? Country = null)
    : IRequest<Result<IPaginate<CustomerListItemDto>>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

public sealed record GetCustomerByIdQuery(Guid Id) : IRequest<Result<CustomerDto>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

/// <summary>
/// Dinamik filtre/sıralama ile müşteri arama. İç içe alanlar noktayla: <c>address.country</c>.
/// </summary>
public sealed record SearchCustomersQuery(DynamicQuery Query, PageRequest Page) : IRequest<Result<IPaginate<CustomerListItemDto>>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

public sealed class SearchCustomersQueryValidator : AbstractValidator<SearchCustomersQuery>
{
    public SearchCustomersQueryValidator()
    {
        RuleFor(q => q.Query).NotNull();
        RuleFor(q => q.Page).ValidPage();
    }
}

public sealed class GetCustomerListQueryValidator : AbstractValidator<GetCustomerListQuery>
{
    public GetCustomerListQueryValidator()
    {
        RuleFor(q => q.Page).ValidPage();
        RuleFor(q => q.Search).MaximumLength(100);
    }
}

public sealed class CustomerQueryHandlers
    : IRequestHandler<GetCustomerListQuery, Result<IPaginate<CustomerListItemDto>>>,
        IRequestHandler<GetCustomerByIdQuery, Result<CustomerDto>>,
        IRequestHandler<SearchCustomersQuery, Result<IPaginate<CustomerListItemDto>>>
{
    private readonly IRepository<Customer, Guid> _customers;
    private readonly IMapper _mapper;

    public CustomerQueryHandlers(IRepository<Customer, Guid> customers, IMapper mapper)
    {
        _customers = customers;
        _mapper = mapper;
    }

    public async Task<Result<IPaginate<CustomerListItemDto>>> Handle(GetCustomerListQuery request, CancellationToken cancellationToken)
    {
        IQueryable<Customer> query = _customers.Query(enableTracking: false);

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            string pattern = $"%{request.Search.Trim().ToUpperInvariant()}%";
            query = query.Where(c =>
                EF.Functions.Like(c.CompanyName.ToUpper(), pattern)
                || EF.Functions.Like(c.Code, pattern)
                || (c.ContactName != null && EF.Functions.Like(c.ContactName.ToUpper(), pattern))
            );
        }

        if (!string.IsNullOrWhiteSpace(request.Country))
        {
            string country = request.Country.Trim();
            query = query.Where(c => c.Address != null && c.Address.Country == country);
        }

        return Result.Ok(
            await query
                .OrderBy(c => c.CompanyName)
                .ProjectTo<CustomerListItemDto>(_mapper)
                .ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken)
        );
    }

    public Task<Result<IPaginate<CustomerListItemDto>>> Handle(SearchCustomersQuery request, CancellationToken cancellationToken) =>
        DynamicSearch
            .Apply(_customers.Query(enableTracking: false), request.Query, q => q.OrderBy(c => c.CompanyName))
            .MapAsync(query =>
                query.ProjectTo<CustomerListItemDto>(_mapper).ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken)
            );

    public Task<Result<CustomerDto>> Handle(GetCustomerByIdQuery request, CancellationToken cancellationToken) =>
        _customers
            .GetByIdAsync(request.Id, enableTracking: false, cancellationToken: cancellationToken)
            .ToResult(CustomerErrors.NotFound(request.Id))
            .Map(customer => _mapper.Map<Customer, CustomerDto>(customer)!);
}
