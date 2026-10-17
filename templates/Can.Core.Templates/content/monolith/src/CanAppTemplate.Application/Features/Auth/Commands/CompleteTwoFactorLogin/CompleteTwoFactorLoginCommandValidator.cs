using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Otp;
using Can.Core.Security.RecoveryCodes;
using Can.Core.Security.Sessions;
using FluentValidation;
using CanAppTemplate.Application.Services.Validation;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.CompleteTwoFactorLogin;

public sealed class CompleteTwoFactorLoginCommandValidator : AbstractValidator<CompleteTwoFactorLoginCommand>
{
    public CompleteTwoFactorLoginCommandValidator()
    {
        RuleFor(c => c.Code).SixDigitCode().When(c => !c.IsRecoveryCode);
        RuleFor(c => c.Code).NotEmpty().MaximumLength(32).When(c => c.IsRecoveryCode);
    }
}
