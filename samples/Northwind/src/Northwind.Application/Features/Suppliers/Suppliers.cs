using Can.Core.Application;
using Can.Core.Mapping;
using Can.Core.Mediator;
using Can.Core.Persistence.Paging;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using Northwind.Application.Common;
using Northwind.Domain.Catalog;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Suppliers;

public sealed record SupplierDto(
    Guid Id,
    string CompanyName,
    string? ContactName,
    string? ContactTitle,
    string? City,
    string? Country,
    string? Phone,
    string? HomePage);

public sealed class SupplierProfile : MappingProfile
{
    public SupplierProfile()
    {
        // Owned Address düzleştirilir; ProjectTo ile SQL'e çevrilir.
        CreateMap<Supplier, SupplierDto>()
            .ForMember(d => d.City, o => o.MapFrom(s => s.Address == null ? null : s.Address.City))
            .ForMember(d => d.Country, o => o.MapFrom(s => s.Address == null ? null : s.Address.Country));
    }
}

public sealed record GetSupplierListQuery(PageRequest Page, string? Country = null) : IRequest<IPaginate<SupplierDto>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

public sealed class GetSupplierListQueryValidator : AbstractValidator<GetSupplierListQuery>
{
    public GetSupplierListQueryValidator() => RuleFor(q => q.Page).ValidPage();
}

public sealed class GetSupplierListQueryHandler : IRequestHandler<GetSupplierListQuery, IPaginate<SupplierDto>>
{
    private readonly IRepository<Supplier, Guid> _suppliers;
    private readonly IMapper _mapper;

    public GetSupplierListQueryHandler(IRepository<Supplier, Guid> suppliers, IMapper mapper)
    {
        _suppliers = suppliers;
        _mapper = mapper;
    }

    public Task<IPaginate<SupplierDto>> Handle(GetSupplierListQuery request, CancellationToken cancellationToken)
    {
        IQueryable<Supplier> query = _suppliers.Query(enableTracking: false);

        if (!string.IsNullOrWhiteSpace(request.Country))
        {
            string country = request.Country.Trim();
            query = query.Where(s => s.Address != null && s.Address.Country == country);
        }

        return query
            .OrderBy(s => s.CompanyName)
            .ProjectTo<SupplierDto>(_mapper)
            .ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken);
    }
}
