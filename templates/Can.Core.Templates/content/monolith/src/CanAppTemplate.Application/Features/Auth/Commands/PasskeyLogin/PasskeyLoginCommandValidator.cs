using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Passkeys;
using Can.Core.Security.Sessions;
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
