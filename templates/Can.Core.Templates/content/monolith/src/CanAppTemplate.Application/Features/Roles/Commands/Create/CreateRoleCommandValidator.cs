using FluentValidation;
using Can.Core.Security.Entities;

namespace CanAppTemplate.Application.Features.Roles.Commands.Create;

public sealed class CreateRoleCommandValidator : AbstractValidator<CreateRoleCommand>
{
    public CreateRoleCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Role<Guid>.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(Role<Guid>.DescriptionMaxLength);
    }
}
