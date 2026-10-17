using Can.Core.Application;
using Can.Core.Application.Requests;
using Can.Core.Application.Responses;
using Can.Core.Mediator;
using Can.Core.Persistence.Paging;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Authorization;
using Can.Core.Security.Entities;
using FluentValidation;
using CanAppTemplate.Application.Features.Roles.Rules;
using CanAppTemplate.Application.Services.Validation;

namespace CanAppTemplate.Application.Features.Roles.Queries.GetList;

public sealed class GetListRoleQuery : IRequest<GetListResponse<GetListRoleListItemDto>>, ISecuredRequest
{
    public PageRequest PageRequest { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [IdentityPermissions.RolesRead];
}

public sealed record GetListRoleListItemDto(Guid Id, string Name, string? Description, bool IsSystem, int PermissionCount);

public sealed class GetListRoleQueryValidator : AbstractValidator<GetListRoleQuery>
{
    public GetListRoleQueryValidator() => RuleFor(x => x.PageRequest).ValidPage();
}

public sealed class GetListRoleQueryHandler : IRequestHandler<GetListRoleQuery, GetListResponse<GetListRoleListItemDto>>
{
    private readonly IRepository<Role<Guid>, Guid> _roleRepository;

    public GetListRoleQueryHandler(IRepository<Role<Guid>, Guid> roleRepository) => _roleRepository = roleRepository;

    public async Task<GetListResponse<GetListRoleListItemDto>> Handle(GetListRoleQuery request, CancellationToken cancellationToken)
    {
        IPaginate<Role<Guid>> roles = await _roleRepository.GetListAsync(
            orderBy: q => q.OrderBy(r => r.Name),
            include: RoleBusinessRules.IncludeOperationClaims,
            index: request.PageRequest.Page,
            size: request.PageRequest.PageSize,
            enableTracking: false,
            cancellationToken: cancellationToken);

        return roles.ToGetListResponse(r => new GetListRoleListItemDto(r.Id, r.Name, r.Description, r.IsSystem, RoleBusinessRules.PermissionNamesOf(r).Count));
    }
}
