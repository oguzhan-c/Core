using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Can.Core.Security.Events;
using Can.Core.Security.Sessions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Application.Features.Auth.Constants;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Features.Auth.Commands.LinkExternalLogin;

/// <summary>
/// Giriş yapmış kullanıcının hesabına dış hesap bağlar. Kullanıcı, sağlayıcıya giden şifreli state'ten gelir (dönüşte
/// oturum cookie'si gönderilmez); istemciden alınmaz.
/// </summary>
public sealed record LinkExternalLoginCommand(Guid UserId, string Provider, string ProviderDisplayName, string ProviderKey)
    : IRequest<Result<Success>>, ITransactionalRequest, ILoggableRequest;

public sealed class LinkExternalLoginCommandHandler : IRequestHandler<LinkExternalLoginCommand, Result<Success>>
{
    private readonly IRepository<AppUser, Guid> _users;
    private readonly ISecurityEvents _events;
    private readonly TimeProvider _timeProvider;

    public LinkExternalLoginCommandHandler(IRepository<AppUser, Guid> users, ISecurityEvents events, TimeProvider timeProvider)
    {
        _users = users;
        _events = events;
        _timeProvider = timeProvider;
    }

    public async Task<Result<Success>> Handle(LinkExternalLoginCommand request, CancellationToken cancellationToken)
    {
        AppUser? user = await _users.GetAsync(u => u.Id == request.UserId, include: q => q.Include(u => u.Logins), cancellationToken: cancellationToken);
        if (user is null)
            return Error.Unauthorized();

        string provider = request.Provider.Trim().ToLowerInvariant();
        string key = request.ProviderKey.Trim();

        if (user.Logins.Any(l => l.LoginProvider == provider && l.ProviderKey == key))
            return Result.Success; // zaten bağlı

        if (await _users.AnyAsync(u => u.Id != user.Id && u.Logins.Any(l => l.LoginProvider == provider && l.ProviderKey == key), cancellationToken: cancellationToken))
            return AuthErrors.ExternalAlreadyLinked;

        if (!user.AddLogin(provider, key, request.ProviderDisplayName, _timeProvider.GetUtcNow()))
            return AuthErrors.ExternalProviderLinked;

        await _events.PublishAsync(
            new SecurityEvent(SecurityEventTypes.ExternalLoginLinked, user.Id.ToString()) { Properties = new Dictionary<string, string?> { ["provider"] = provider } },
            cancellationToken
        );
        return Result.Success;
    }
}
