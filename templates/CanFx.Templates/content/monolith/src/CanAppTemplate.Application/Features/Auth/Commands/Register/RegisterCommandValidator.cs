using CanFx.Application;
using CanFx.Domain.Results;
using CanFx.Mailing;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Entities;
using CanFx.Security.Events;
using CanFx.Security.Hashing;
using CanFx.Security.Sessions;
using CanFx.Security.VerificationCodes;
using FluentValidation;
using CanAppTemplate.Application.Services.Validation;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.Register;

public sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(256);
        RuleFor(c => c.Password).StrongPassword();
        RuleFor(c => c.FirstName).NotEmpty().MaximumLength(AppUser.NameMaxLength);
        RuleFor(c => c.LastName).NotEmpty().MaximumLength(AppUser.NameMaxLength);
        RuleFor(c => c.PhoneNumber).MaximumLength(AppUser.PhoneMaxLength);
    }
}
