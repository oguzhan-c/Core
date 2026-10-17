using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Otp;
using Can.Core.Security.Passkeys;
using Can.Core.Security.RecoveryCodes;
using Fido2NetLib;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.CompletePasskeyRegistration;

public sealed class CompletePasskeyRegistrationCommandValidator : AbstractValidator<CompletePasskeyRegistrationCommand>
{
    public CompletePasskeyRegistrationCommandValidator()
    {
        RuleFor(c => c.Response).NotNull();
        RuleFor(c => c.Options).NotNull();
        RuleFor(c => c.Name).MaximumLength(PasskeyLimits.NameMaxLength);
    }
}
