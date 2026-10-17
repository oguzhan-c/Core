using FluentValidation;
using Can.Core.Security.Entities;

namespace CanAppTemplate.Application.Features.Roles.Commands.Update;

public sealed class UpdateRoleCommandValidator : AbstractValidator<UpdateRoleCommand>
{
    public UpdateRoleCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Role<Guid>.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(Role<Guid>.DescriptionMaxLength);
    }
}
