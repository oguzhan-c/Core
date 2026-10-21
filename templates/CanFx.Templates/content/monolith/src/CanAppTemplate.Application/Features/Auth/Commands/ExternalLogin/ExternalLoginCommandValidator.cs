using CanFx.Application;
using CanFx.Domain.Results;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Entities;
using CanFx.Security.Events;
using CanFx.Security.Sessions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.ExternalLogin;

public sealed class ExternalLoginCommandValidator : AbstractValidator<ExternalLoginCommand>
{
    public ExternalLoginCommandValidator()
    {
        RuleFor(c => c.Provider).NotEmpty().MaximumLength(UserLogin<IdentityKey>.ProviderMaxLength);
        RuleFor(c => c.ProviderKey).NotEmpty().MaximumLength(UserLogin<IdentityKey>.ProviderKeyMaxLength);
        RuleFor(c => c.Email).MaximumLength(256);
    }
}
