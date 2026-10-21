using FluentValidation;

namespace CanAppTemplate.Application.Features.Roles.Commands.Delete;

public sealed class DeleteRoleCommandValidator : AbstractValidator<DeleteRoleCommand>
{
    public DeleteRoleCommandValidator() => RuleFor(x => x.Id).NotEmpty();
}
