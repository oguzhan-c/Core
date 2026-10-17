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
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Application.Services.AuthService;

/// <summary>Kurtarma kodlarını yeniler: eskiler silinir, yeniler kaydedilir.</summary>
public sealed class RecoveryCodeIssuer
{
    private readonly IRepository<RecoveryCode<Guid>, Guid> _codes;
    private readonly IRecoveryCodeService _service;
    private readonly ISecurityEvents _events;
    private readonly TimeProvider _timeProvider;

    public RecoveryCodeIssuer(IRepository<RecoveryCode<Guid>, Guid> codes, IRecoveryCodeService service, ISecurityEvents events, TimeProvider timeProvider)
    {
        _codes = codes;
        _service = service;
        _events = events;
        _timeProvider = timeProvider;
    }

    public async Task<RecoveryCodesDto> RegenerateAsync(Guid userId, CancellationToken cancellationToken)
    {
        await DeleteAllAsync(userId, cancellationToken);

        RecoveryCodeSet set = _service.Generate();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await _codes.AddRangeAsync(set.CodeHashes.Select(hash => new RecoveryCode<Guid>(userId, hash, now)), cancellationToken);
        await _events.PublishAsync(new SecurityEvent(SecurityEventTypes.RecoveryCodesGenerated, userId.ToString()), cancellationToken);
        return new RecoveryCodesDto(set.Codes);
    }

    public async Task DeleteAllAsync(Guid userId, CancellationToken cancellationToken)
    {
        List<RecoveryCode<Guid>> existing = await _codes.Query().Where(c => c.UserId == userId).ToListAsync(cancellationToken);
        _codes.DeleteRange(existing, permanent: true);
    }
}
