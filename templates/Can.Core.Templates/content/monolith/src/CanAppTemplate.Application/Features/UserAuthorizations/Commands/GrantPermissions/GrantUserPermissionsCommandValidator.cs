using FluentValidation;

namespace CanAppTemplate.Application.Features.UserAuthorizations.Commands.GrantPermissions;

public sealed class GrantUserPermissionsCommandValidator : AbstractValidator<GrantUserPermissionsCommand>
{
    public GrantUserPermissionsCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.OperationClaimIds).NotEmpty();
    }
}
