using CanFx.Application;
using CanFx.Domain.Results;
using CanFx.Mediator;
using CanFx.Persistence.Repositories;
using CanFx.Security.Entities;
using CanFx.Security.Events;
using CanFx.Security.Otp;
using CanFx.Security.Passkeys;
using CanFx.Security.RecoveryCodes;
using Fido2NetLib;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Services.AuthService;

/// <summary>Kurtarma kodlarını yeniler: eskiler silinir, yeniler kaydedilir.</summary>
public sealed class RecoveryCodeIssuer
{
    private readonly IRepository<RecoveryCode<IdentityKey>, IdentityKey> _codes;
    private readonly IRecoveryCodeService _service;
    private readonly ISecurityEvents _events;
    private readonly TimeProvider _timeProvider;

    public RecoveryCodeIssuer(IRepository<RecoveryCode<IdentityKey>, IdentityKey> codes, IRecoveryCodeService service, ISecurityEvents events, TimeProvider timeProvider)
    {
        _codes = codes;
        _service = service;
        _events = events;
        _timeProvider = timeProvider;
    }

    public async Task<RecoveryCodesDto> RegenerateAsync(IdentityKey userId, CancellationToken cancellationToken)
    {
        await DeleteAllAsync(userId, cancellationToken);

        RecoveryCodeSet set = _service.Generate();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await _codes.AddRangeAsync(set.CodeHashes.Select(hash => new RecoveryCode<IdentityKey>(userId, hash, now)), cancellationToken);
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.RecoveryCodesGenerated, userId.ToString()), cancellationToken);
        return new RecoveryCodesDto(set.Codes);
    }

    public async Task DeleteAllAsync(IdentityKey userId, CancellationToken cancellationToken)
    {
        List<RecoveryCode<IdentityKey>> existing = await _codes.Query().Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        _codes.DeleteRange(existing, permanent: true);
    }
}
