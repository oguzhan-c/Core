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

namespace CanAppTemplate.Application.Features.Account.Commands.RenamePasskey;

public sealed record RenamePasskeyCommand(Guid Id, string Name) : IRequest<Result<Success>>, ISecuredRequest, ITransactionalRequest;

public sealed class RenamePasskeyCommandHandler : IRequestHandler<RenamePasskeyCommand, Result<Success>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<UserPasskey<Guid>, Guid> _passkeys;

    public RenamePasskeyCommandHandler(ICurrentUser currentUser, IRepository<UserPasskey<Guid>, Guid> passkeys)
    {
        _currentUser = currentUser;
        _passkeys = passkeys;
    }

    public Task<Result<Success>> Handle(RenamePasskeyCommand request, CancellationToken cancellationToken) =>
        _currentUser
            .UserId()
            .ThenAsync(userId => _passkeys.GetAsync(p => p.Id == request.Id && p.UserId == userId, cancellationToken: cancellationToken).ToResult(AccountUsers.PasskeyNotFound))
            .Map(passkey =>
            {
                passkey.Rename(request.Name);
                return Result.Success;
            });
}
