using Can.Core.Security.Entities;
using FluentValidation;

namespace CanAppTemplate.Application.Features.Roles.Commands.Create;

public sealed class CreateRoleCommandValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Role<IdentityKey>.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(Role<IdentityKey>.DescriptionMaxLength);
    }
}
