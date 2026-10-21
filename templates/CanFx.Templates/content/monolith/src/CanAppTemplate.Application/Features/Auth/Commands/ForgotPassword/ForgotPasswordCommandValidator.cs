using CanFx.Application;
using CanFx.Domain.Results;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Events;
using CanFx.Security.Hashing;
using CanFx.Security.Sessions;
using CanFx.Security.Tokens;
using FluentValidation;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.ForgotPassword;

public sealed class ForgotPasswordCommandValidator : AbstractValidator<ForgotPasswordCommand>
{
    public ForgotPasswordCommandValidator() => RuleFor(c => c.Email).NotEmpty().EmailAddress().MaximumLength(256);
}
