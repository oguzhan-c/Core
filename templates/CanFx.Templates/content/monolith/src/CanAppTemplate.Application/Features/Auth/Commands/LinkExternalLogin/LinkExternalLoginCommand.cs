using CanFx.Application;
using CanFx.Domain.Results;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Entities;
using CanFx.Security.Events;
using CanFx.Security.Sessions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Application.Features.Auth.Rules;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.LinkExternalLogin;

/// <summary>
/// Giriş yapmış kullanıcının hesabına dış hesap bağlar. Kullanıcı, sağlayıcıya giden şifreli state'ten gelir (dönüşte
/// oturum cookie'si gönderilmez); istemciden alınmaz.
/// </summary>
public sealed record LinkExternalLoginCommand(IdentityKey UserId, string Provider, string ProviderDisplayName, string ProviderKey)
    : IRequest, ITransactionalRequest, ILoggableRequest;

public sealed class LinkExternalLoginCommandHandler : IRequestHandler<LinkExternalLoginCommand>
{
    private readonly IRepository<AppUser, IdentityKey> _users;
    private readonly ISecurityEvents _events;
    private readonly TimeProvider _timeProvider;
    private readonly AuthBusinessRules _authBusinessRules;

    public LinkExternalLoginCommandHandler(IRepository<AppUser, IdentityKey> users, ISecurityEvents events, TimeProvider timeProvider, AuthBusinessRules authBusinessRules)
    {
        _users = users;
        _events = events;
        _timeProvider = timeProvider;
        _authBusinessRules = authBusinessRules;
    }

    public async Task Handle(LinkExternalLoginCommand request, CancellationToken cancellationToken)
    {
        AppUser? user = await _users.GetAsync(u => u.Id == request.UserId, include: q => q.Include(u => u.Logins), cancellationToken: cancellationToken);
        _authBusinessRules.UserShouldExist(user is not null);

        string provider = request.Provider.Trim().ToLowerInvariant();
        string key = request.ProviderKey.Trim();

        if (user.Logins.Any(l => l.LoginProvider == provider && l.ProviderKey == key))
            return; // zaten bağlı

        await _authBusinessRules.ExternalLoginShouldNotBelongToAnotherUser(user.Id, provider, key, cancellationToken);
        _authBusinessRules.ExternalLoginShouldBeAdded(user.AddLogin(provider, key, request.ProviderDisplayName, _timeProvider.GetUtcNow()));

        await _events.PublishAsync(
            new SecurityEvent(SecurityEventTypes.ExternalLoginLinked, user.Id.ToString()) { Properties = new Dictionary<string, string?> { ["provider"] = provider } },
            cancellationToken
        );
    }
}
