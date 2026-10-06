using Can.Core.Domain.Results;
using Can.Core.Application;
using Can.Core.Mapping;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Northwind.Domain.Shipping;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Shippers;

public sealed record ShipperDto(Guid Id, string CompanyName, string? Phone);

public sealed class ShipperProfile : MappingProfile
{
    public ShipperProfile()
    {
        CreateMap<Shipper, ShipperDto>();
    }
}

public sealed record GetShipperListQuery : IRequest<Result<IReadOnlyList<ShipperDto>>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => AppRoles.Staff;
}

public sealed class GetShipperListQueryHandler : IRequestHandler<GetShipperListQuery, Result<IReadOnlyList<ShipperDto>>>
{
    private readonly IRepository<Shipper, Guid> _shippers;
    private readonly IMapper _mapper;

    public GetShipperListQueryHandler(IRepository<Shipper, Guid> shippers, IMapper mapper)
    {
        _shippers = shippers;
        _mapper = mapper;
    }

    public async Task<Result<IReadOnlyList<ShipperDto>>> Handle(GetShipperListQuery request, CancellationToken cancellationToken) =>
        Result.Ok<IReadOnlyList<ShipperDto>>(
            await _shippers.Query(enableTracking: false).OrderBy(s => s.CompanyName).ProjectTo<ShipperDto>(_mapper).ToListAsync(cancellationToken)
        );
}
