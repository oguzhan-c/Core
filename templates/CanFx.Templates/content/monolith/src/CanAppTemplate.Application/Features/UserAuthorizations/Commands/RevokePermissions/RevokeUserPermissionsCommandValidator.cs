using FluentValidation;

namespace CanAppTemplate.Application.Features.UserAuthorizations.Commands.RevokePermissions;

public sealed class RevokeUserPermissionsCommandValidator : AbstractValidator<RevokeUserPermissionsCommand>
{
    public RevokeUserPermissionsCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.OperationClaimIds).NotEmpty();
    }
}
