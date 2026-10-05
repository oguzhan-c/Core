using Can.Core.Mapping;
using Northwind.Domain.Common;

namespace Northwind.Application.Common;

public sealed class CommonProfile : MappingProfile
{
    public CommonProfile()
    {
        CreateMap<Address, AddressDto>();
    }
}
