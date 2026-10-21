using CanFx.Application;
using CanFx.Domain.Results;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Entities;
using CanFx.Security.Events;
using CanFx.Security.Passkeys;
using CanFx.Security.Sessions;
using Fido2NetLib;
using FluentValidation;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.PasskeyLogin;

public sealed class PasskeyLoginCommandValidator : AbstractValidator<PasskeyLoginCommand>
{
    public PasskeyLoginCommandValidator()
    {
        RuleFor(c => c.Response).NotNull();
        RuleFor(c => c.Options).NotNull();
    }
}
