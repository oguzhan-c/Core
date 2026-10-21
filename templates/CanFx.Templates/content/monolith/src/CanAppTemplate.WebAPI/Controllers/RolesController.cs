using CanFx.Application.Requests;
using CanFx.Application.Responses;
using CanFx.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using CanAppTemplate.Application.Features.OperationClaims.Queries.GetList;
using CanAppTemplate.Application.Features.Roles.Commands.Create;
using CanAppTemplate.Application.Features.Roles.Commands.Delete;
using CanAppTemplate.Application.Features.Roles.Commands.GrantPermissions;
using CanAppTemplate.Application.Features.Roles.Commands.RevokePermissions;
using CanAppTemplate.Application.Features.Roles.Commands.Update;
using CanAppTemplate.Application.Features.Roles.Queries.GetById;
using CanAppTemplate.Application.Features.Roles.Queries.GetList;

namespace CanAppTemplate.WebAPI.Controllers;

/// <summary>
/// Rol yönetimi (<c>/api/identity/roles</c>) ve yetki kataloğu (<c>/api/identity/operation-claims</c>). Yetkiler handler'larda
/// denetlenir (<c>IdentityPermissions</c>); yetki yükseltme kuralları Core'daki <c>IAccessGrantGuard</c>'dadır.
/// </summary>
[Route("api/identity")]
[ApiController]
public class RolesController : BaseController
{
    [HttpGet("roles")]
    public async Task<IActionResult> GetList([FromQuery] PageRequest pageRequest, CancellationToken cancellationToken)
    {
        GetListResponse<GetListRoleListItemDto> response = await Mediator.Send(new GetListRoleQuery { PageRequest = pageRequest }, cancellationToken);
        return Ok(response);
    }

    [HttpGet("roles/{id}")]
    public async Task<IActionResult> GetById([FromRoute] IdentityKey id, CancellationToken cancellationToken)
    {
        GetByIdRoleResponse response = await Mediator.Send(new GetByIdRoleQuery { Id = id }, cancellationToken);
        return Ok(response);
    }

    [HttpPost("roles")]
    public async Task<IActionResult> Add([FromBody] CreateRoleCommand createRoleCommand, CancellationToken cancellationToken)
    {
        CreatedRoleResponse response = await Mediator.Send(createRoleCommand, cancellationToken);
        return Created(uri: $"api/identity/roles/{response.Id}", response);
    }

    [HttpPut("roles/{id}")]
    public async Task<IActionResult> Update([FromRoute] IdentityKey id, [FromBody] UpdateRoleCommand updateRoleCommand, CancellationToken cancellationToken)
    {
        updateRoleCommand.Id = id;
        UpdatedRoleResponse response = await Mediator.Send(updateRoleCommand, cancellationToken);
        return Ok(response);
    }

    [HttpDelete("roles/{id}")]
    public async Task<IActionResult> Delete([FromRoute] IdentityKey id, CancellationToken cancellationToken)
    {
        DeletedRoleResponse response = await Mediator.Send(new DeleteRoleCommand { Id = id }, cancellationToken);
        return Ok(response);
    }

    /// <summary>Gövde: <c>{ "operationClaimIds": ["..."] }</c>.</summary>
    [HttpPost("roles/{id}/permissions/grant")]
    public async Task<IActionResult> GrantPermissions([FromRoute] IdentityKey id, [FromBody] GrantRolePermissionsCommand command, CancellationToken cancellationToken)
    {
        command.RoleId = id;
        GrantedRolePermissionsResponse response = await Mediator.Send(command, cancellationToken);
        return Ok(response);
    }

    /// <summary>Gövde: <c>{ "operationClaimIds": ["..."] }</c>.</summary>
    [HttpPost("roles/{id}/permissions/revoke")]
    public async Task<IActionResult> RevokePermissions([FromRoute] IdentityKey id, [FromBody] RevokeRolePermissionsCommand command, CancellationToken cancellationToken)
    {
        command.RoleId = id;
        RevokedRolePermissionsResponse response = await Mediator.Send(command, cancellationToken);
        return Ok(response);
    }

    /// <summary>Yetki kataloğu (kodla tanımlanır; buradan oluşturulmaz / silinmez).</summary>
    [HttpGet("operation-claims")]
    public async Task<IActionResult> GetOperationClaims([FromQuery] bool includeDeprecated, CancellationToken cancellationToken)
    {
        IReadOnlyList<GetListOperationClaimListItemDto> response = await Mediator.Send(new GetListOperationClaimQuery { IncludeDeprecated = includeDeprecated }, cancellationToken);
        return Ok(response);
    }
}
