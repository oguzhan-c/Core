using CanFx.WebApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using CanAppTemplate.Application.Features.UserAuthorizations.Commands.AssignRole;
using CanAppTemplate.Application.Features.UserAuthorizations.Commands.GrantPermissions;
using CanAppTemplate.Application.Features.UserAuthorizations.Commands.RemoveRole;
using CanAppTemplate.Application.Features.UserAuthorizations.Commands.RevokePermissions;
using CanAppTemplate.Application.Features.UserAuthorizations.Queries.GetByUserId;

namespace CanAppTemplate.WebAPI.Controllers;

/// <summary>
/// Kullanıcının rolleri ve doğrudan yetkileri (<c>/api/identity/users/{userId}</c>). Değişiklikler token süresini beklemeden
/// sonraki istekte geçerli olur (<c>UseCanPermissionResolution</c>).
/// </summary>
[Route("api/identity/users/{userId}")]
[ApiController]
public class UserAuthorizationsController : BaseController
{
    [HttpGet("authorization")]
    public async Task<IActionResult> Get([FromRoute] IdentityKey userId, CancellationToken cancellationToken)
    {
        GetByUserIdUserAuthorizationResponse response = await Mediator.Send(new GetByUserIdUserAuthorizationQuery { UserId = userId }, cancellationToken);
        return Ok(response);
    }

    [HttpPost("roles/{roleId}")]
    public async Task<IActionResult> AssignRole([FromRoute] IdentityKey userId, [FromRoute] IdentityKey roleId, CancellationToken cancellationToken)
    {
        AssignedUserRoleResponse response = await Mediator.Send(new AssignUserRoleCommand { UserId = userId, RoleId = roleId }, cancellationToken);
        return Ok(response);
    }

    [HttpDelete("roles/{roleId}")]
    public async Task<IActionResult> RemoveRole([FromRoute] IdentityKey userId, [FromRoute] IdentityKey roleId, CancellationToken cancellationToken)
    {
        RemovedUserRoleResponse response = await Mediator.Send(new RemoveUserRoleCommand { UserId = userId, RoleId = roleId }, cancellationToken);
        return Ok(response);
    }

    /// <summary>Gövde: <c>{ "operationClaimIds": ["..."] }</c>.</summary>
    [HttpPost("permissions/grant")]
    public async Task<IActionResult> GrantPermissions([FromRoute] IdentityKey userId, [FromBody] GrantUserPermissionsCommand command, CancellationToken cancellationToken)
    {
        command.UserId = userId;
        GrantedUserPermissionsResponse response = await Mediator.Send(command, cancellationToken);
        return Ok(response);
    }

    /// <summary>Gövde: <c>{ "operationClaimIds": ["..."] }</c>.</summary>
    [HttpPost("permissions/revoke")]
    public async Task<IActionResult> RevokePermissions([FromRoute] IdentityKey userId, [FromBody] RevokeUserPermissionsCommand command, CancellationToken cancellationToken)
    {
        command.UserId = userId;
        RevokedUserPermissionsResponse response = await Mediator.Send(command, cancellationToken);
        return Ok(response);
    }
}
