using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Sessions;
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
