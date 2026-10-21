using FluentValidation;

namespace CanAppTemplate.Application.Features.Roles.Commands.RevokePermissions;

public sealed class RevokeRolePermissionsCommandValidator : AbstractValidator<RevokeRolePermissionsCommand>
{
    public RevokeRolePermissionsCommandValidator()
    {
        RuleFor(x => x.RoleId).NotEmpty();
        RuleFor(x => x.OperationClaimIds).NotEmpty();
    }
}
