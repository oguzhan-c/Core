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

namespace CanAppTemplate.Application.Services.AuthService;

internal static class AccountUsers
{
    public static async Task<Result<AppUser>> CurrentAsync(ICurrentUser currentUser, IRepository<AppUser, IdentityKey> users, CancellationToken cancellationToken)
    {
        Result<IdentityKey> id = currentUser.UserId();
        if (id.IsFailure)
            return id.Errors;

        return await users.GetByIdAsync(id.Value, cancellationToken: cancellationToken).ToResult(Error.Unauthorized());
    }

    public static readonly Error PasskeyNotFound = Error.NotFound("passkey.not_found", "Passkey bulunamadı.");
}
