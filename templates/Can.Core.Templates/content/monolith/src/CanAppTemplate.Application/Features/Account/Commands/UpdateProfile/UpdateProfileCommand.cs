using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using CanAppTemplate.Application.Features.Account.Rules;
using CanAppTemplate.Application.Features.Auth.Rules;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.UpdateProfile;

/// <summary>Ad, soyad, telefon ve kampanya izni.</summary>
public sealed record UpdateProfileCommand(string FirstName, string LastName, string? PhoneNumber, bool MarketingConsent)
    : IRequest, ISecuredRequest, ITransactionalRequest;

public sealed class UpdateProfileCommandHandler : IRequestHandler<UpdateProfileCommand>
{
    private readonly AuthBusinessRules _authBusinessRules;

    public UpdateProfileCommandHandler(AuthBusinessRules authBusinessRules) => _authBusinessRules = authBusinessRules;

    public async Task Handle(UpdateProfileCommand request, CancellationToken cancellationToken)
    {
        AppUser user = await _authBusinessRules.CurrentUserShouldExist(cancellationToken: cancellationToken);
        user.UpdateProfile(request.FirstName, request.LastName, request.PhoneNumber, request.MarketingConsent).OrThrow();
    }
}
