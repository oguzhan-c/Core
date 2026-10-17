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

namespace CanAppTemplate.Application.Features.Account.Commands.BeginPasskeyRegistration;

/// <summary>Passkey kaydının ilk adımı: tarayıcıya verilecek seçenekler (WebApi bunları kısa süre saklar).</summary>
public sealed record BeginPasskeyRegistrationCommand : IRequest<Result<CredentialCreateOptions>>, ISecuredRequest;

public sealed class BeginPasskeyRegistrationCommandHandler : IRequestHandler<BeginPasskeyRegistrationCommand, Result<CredentialCreateOptions>>
{
    private readonly ICurrentUser _currentUser;
    private readonly IRepository<AppUser, Guid> _users;
    private readonly IRepository<UserPasskey<Guid>, Guid> _passkeys;
    private readonly IPasskeyService _passkeyService;

    public BeginPasskeyRegistrationCommandHandler(
        ICurrentUser currentUser,
        IRepository<AppUser, Guid> users,
        IRepository<UserPasskey<Guid>, Guid> passkeys,
        IPasskeyService passkeyService)
    {
        _currentUser = currentUser;
        _users = users;
        _passkeys = passkeys;
        _passkeyService = passkeyService;
    }

    public async Task<Result<CredentialCreateOptions>> Handle(BeginPasskeyRegistrationCommand request, CancellationToken cancellationToken)
    {
        Result<AppUser> current = await AccountUsers.CurrentAsync(_currentUser, _users, cancellationToken);
        if (current.IsFailure)
            return current.Errors;

        AppUser user = current.Value;
        List<byte[]> existing = await _passkeys.Query(enableTracking: false).Where(p => p.UserId == user.Id).Select(p => p.CredentialId).ToListAsync(cancellationToken);

        if (existing.Count >= PasskeyLimits.MaxPerUser)
            return Error.Failure("passkey.limit", $"En fazla {PasskeyLimits.MaxPerUser} passkey eklenebilir; önce birini sil.");

        return _passkeyService.BeginRegistration(new PasskeyUser(user.PasskeyUserHandle, user.Email, user.FullName), existing);
    }
}
