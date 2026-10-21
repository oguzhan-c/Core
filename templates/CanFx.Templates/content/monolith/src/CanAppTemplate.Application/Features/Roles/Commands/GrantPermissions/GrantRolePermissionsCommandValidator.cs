using FluentValidation;

namespace CanAppTemplate.Application.Features.Roles.Commands.GrantPermissions;

public sealed class GrantRolePermissionsCommandValidator : AbstractValidator<GrantRolePermissionsCommand>
{
    public GrantRolePermissionsCommandValidator()
    {
        RuleFor(x => x.RoleId).NotEmpty();
        RuleFor(x => x.OperationClaimIds).NotEmpty();
    }
}
