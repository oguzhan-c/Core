using Can.Core.Application;
using Can.Core.Application.Exceptions;
using Can.Core.Application.Rules;
using Can.Core.Domain.Exceptions;
using Can.Core.Mapping;
using Can.Core.Mediator;
using Can.Core.Persistence.Dynamic;
using Can.Core.Persistence.Paging;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Common;
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

    public async Task<Customer> MustExistAsync(Guid id, CancellationToken cancellationToken) =>
        await _customers.GetByIdAsync(id, cancellationToken: cancellationToken) ?? throw NotFoundException.For<Customer>(id);

    /// <summary>Silinmiş müşterilerin kodları da tekrar kullanılamaz (geçmiş siparişlerde görünür).</summary>
    public async Task CodeMustBeUniqueAsync(string code, CancellationToken cancellationToken)
    {
        string normalized = Customer.NormalizeCode(code);
        if (await _customers.AnyAsync(c => c.Code == normalized, withDeleted: true, cancellationToken: cancellationToken))
            throw new ConflictException($"'{normalized}' kodlu bir müşteri zaten var.");
    }

    public async Task MustHaveNoOpenOrdersAsync(Guid customerId, CancellationToken cancellationToken)
    {
        if (await _orders.AnyAsync(o => o.CustomerId == customerId && o.Status == OrderStatus.Placed, cancellationToken: cancellationToken))
            throw new BusinessException("Açık siparişi olan müşteri silinemez.");
    }
}

// ---------------------------------------------------------------- command'lar

public sealed record CreateCustomerCommand(
    string Code,
    string CompanyName,
    string? ContactName,
    string? ContactTitle,
    AddressDto? Address,
    string? Phone,
    string? Fax) : IRequest<Guid>, ISecuredRequest, ITransactionalRequest
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
    string? Fax) : IRequest, ISecuredRequest, ITransactionalRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Sales];
}

public sealed record DeleteCustomerCommand(Guid Id) : IRequest, ISecuredRequest, ITransactionalRequest
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
    : IRequestHandler<CreateCustomerCommand, Guid>,
        IRequestHandler<UpdateCustomerCommand>,
        IRequestHandler<DeleteCustomerCommand>
{
    private readonly IRepository<Customer, Guid> _customers;
    private readonly CustomerBusinessRules _rules;

    public CustomerCommandHandlers(IRepository<Customer, Guid> customers, CustomerBusinessRules rules)
    {
        _customers = customers;
        _rules = rules;
    }

    public async Task<Guid> Handle(CreateCustomerCommand request, CancellationToken cancellationToken)
    {
        await _rules.CodeMustBeUniqueAsync(request.Code, cancellationToken);

        Customer customer = Customer.Create(
            request.Code,
            request.CompanyName,
            request.ContactName,
            request.ContactTitle,
            request.Address?.ToAddress(),
            request.Phone,
            request.Fax
        );

        await _customers.AddAsync(customer, cancellationToken);
        return customer.Id;
    }

    public async Task Handle(UpdateCustomerCommand request, CancellationToken cancellationToken)
    {
        Customer customer = await _rules.MustExistAsync(request.Id, cancellationToken);
        customer.Update(request.CompanyName, request.ContactName, request.ContactTitle, request.Address?.ToAddress(), request.Phone, request.Fax);
    }

    public async Task Handle(DeleteCustomerCommand request, CancellationToken cancellationToken)
    {
        Customer customer = await _rules.MustExistAsync(request.Id, cancellationToken);
        await _rules.MustHaveNoOpenOrdersAsync(request.Id, cancellationToken);
        _customers.Delete(customer);
    }
}

// ---------------------------------------------------------------- query'ler

public sealed record GetCustomerListQuery(PageRequest Page, string? Search = null, string? Country = null)
    : IRequest<IPaginate<CustomerListItemDto>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

public sealed record GetCustomerByIdQuery(Guid Id) : IRequest<CustomerDto>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

/// <summary>
/// Dinamik filtre/sıralama ile müşteri arama. İç içe alanlar noktayla: <c>address.country</c>.
/// </summary>
public sealed record SearchCustomersQuery(DynamicQuery Query, PageRequest Page) : IRequest<IPaginate<CustomerListItemDto>>, ISecuredRequest
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
    : IRequestHandler<GetCustomerListQuery, IPaginate<CustomerListItemDto>>,
        IRequestHandler<GetCustomerByIdQuery, CustomerDto>,
        IRequestHandler<SearchCustomersQuery, IPaginate<CustomerListItemDto>>
{
    private readonly IRepository<Customer, Guid> _customers;
    private readonly IMapper _mapper;

    public CustomerQueryHandlers(IRepository<Customer, Guid> customers, IMapper mapper)
    {
        _customers = customers;
        _mapper = mapper;
    }

    public Task<IPaginate<CustomerListItemDto>> Handle(GetCustomerListQuery request, CancellationToken cancellationToken)
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

        return query
            .OrderBy(c => c.CompanyName)
            .ProjectTo<CustomerListItemDto>(_mapper)
            .ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken);
    }

    public Task<IPaginate<CustomerListItemDto>> Handle(SearchCustomersQuery request, CancellationToken cancellationToken) =>
        DynamicSearch
            .Apply(_customers.Query(enableTracking: false), request.Query, q => q.OrderBy(c => c.CompanyName))
            .ProjectTo<CustomerListItemDto>(_mapper)
            .ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken);

    public async Task<CustomerDto> Handle(GetCustomerByIdQuery request, CancellationToken cancellationToken)
    {
        Customer customer =
            await _customers.GetByIdAsync(request.Id, enableTracking: false, cancellationToken: cancellationToken)
            ?? throw NotFoundException.For<Customer>(request.Id);

        return _mapper.Map<Customer, CustomerDto>(customer)!;
    }
}
