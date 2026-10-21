using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using FluentValidation;
using CanAppTemplate.Application.Services.AuthService;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Account.Commands.UpdateProfile;

/// <summary>Ad, soyad, telefon ve kampanya izni.</summary>
public sealed record UpdateProfileCommand(string FirstName, string LastName, string? PhoneNumber, bool MarketingConsent)
    : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest;

public sealed class UpdateProfileCommandHandler : IRequestHandler<UpdateProfileCommand, Result<Success>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, IdentityKey> _users;

    public UpdateProfileCommandHandler(ICurrentUser currentUser, IRepository<AppUser, IdentityKey> users)
    {
        _currentUser = currentUser;
        _users = users;
    }

    public Task<Result<Success>> Handle(UpdateProfileCommand request, CancellationToken cancellationToken) =>
        _currentUser
            .UserId()
            .ThenAsync(id => _users.GetByIdAsync(id, cancellationToken: cancellationToken).ToResult(Error.Unauthorized()))
            .Then(user => user.UpdateProfile(request.FirstName, request.LastName, request.PhoneNumber, request.MarketingConsent));
}
