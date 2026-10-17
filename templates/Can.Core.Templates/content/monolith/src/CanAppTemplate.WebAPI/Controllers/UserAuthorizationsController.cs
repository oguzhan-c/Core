using Can.Core.WebApi.Controllers;
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
[Route("api/identity/users/{userId:guid}")]
[ApiController]
public class UserAuthorizationsController : BaseController
{
    [HttpGet("authorization")]
    public async Task<IActionResult> Get([FromRoute] Guid userId, CancellationToken cancellationToken)
    {
        UserAuthorizationResponse response = await Mediator.Send(new GetUserAuthorizationQuery { UserId = userId }, cancellationToken);
        return Ok(response);
    }

    [HttpPost("roles/{roleId:guid}")]
    public async Task<IActionResult> AssignRole([FromRoute] Guid userId, [FromRoute] Guid roleId, CancellationToken cancellationToken)
    {
        UserAuthorizationResponse response = await Mediator.Send(new AssignUserRoleCommand { UserId = userId, RoleId = roleId }, cancellationToken);
        return Ok(response);
    }

    [HttpDelete("roles/{roleId:guid}")]
    public async Task<IActionResult> RemoveRole([FromRoute] Guid userId, [FromRoute] Guid roleId, CancellationToken cancellationToken)
    {
        UserAuthorizationResponse response = await Mediator.Send(new RemoveUserRoleCommand { UserId = userId, RoleId = roleId }, cancellationToken);
        return Ok(response);
    }

    /// <summary>Gövde: <c>{ "operationClaimIds": ["..."] }</c>.</summary>
    [HttpPost("permissions/grant")]
    public async Task<IActionResult> GrantPermissions([FromRoute] Guid userId, [FromBody] GrantUserPermissionsCommand command, CancellationToken cancellationToken)
    {
        command.UserId = userId;
        UserAuthorizationResponse response = await Mediator.Send(command, cancellationToken);
        return Ok(response);
    }

    /// <summary>Gövde: <c>{ "operationClaimIds": ["..."] }</c>.</summary>
    [HttpPost("permissions/revoke")]
    public async Task<IActionResult> RevokePermissions([FromRoute] Guid userId, [FromBody] RevokeUserPermissionsCommand command, CancellationToken cancellationToken)
    {
        command.UserId = userId;
        UserAuthorizationResponse response = await Mediator.Send(command, cancellationToken);
        return Ok(response);
    }
}
