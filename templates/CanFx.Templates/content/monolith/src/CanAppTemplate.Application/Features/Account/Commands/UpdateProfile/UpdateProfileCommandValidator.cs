using CanFx.Application;
using CanFx.Domain.Results;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using FluentValidation;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.UpdateProfile;

public sealed class UpdateProfileCommandValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileCommandValidator()
    {
        RuleFor(c => c.FirstName).NotEmpty().MaximumLength(AppUser.NameMaxLength);
        RuleFor(c => c.LastName).NotEmpty().MaximumLength(AppUser.NameMaxLength);
        RuleFor(c => c.PhoneNumber).MaximumLength(AppUser.PhoneMaxLength);
    }
}
