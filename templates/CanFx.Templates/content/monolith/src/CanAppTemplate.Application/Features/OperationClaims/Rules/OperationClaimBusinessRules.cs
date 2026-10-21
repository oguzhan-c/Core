using CanFx.Application.Exceptions;
using CanFx.Application.Rules;
using CanFx.Domain.Exceptions;
using CanFx.Persistence.Repositories;
using CanFx.Security.Entities;
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Application.Features.OperationClaims.Constants;

namespace CanAppTemplate.Application.Features.OperationClaims.Rules;

/// <summary>Yetki kataloğu kuralları (rollere ve kullanıcılara yetki verirken kullanılır).</summary>
public sealed class OperationClaimBusinessRules : BaseBusinessRules
{
    private readonly IRepository<OperationClaim<IdentityKey>, IdentityKey> _operationClaimRepository;

    public OperationClaimBusinessRules(IRepository<OperationClaim<IdentityKey>, IdentityKey> operationClaimRepository) =>
        _operationClaimRepository = operationClaimRepository;

    /// <summary>Katalogdaki yetkileri getirir; biri yoksa 404, verilirken kullanımdan kalkmışsa 400.</summary>
    public async Task<IReadOnlyList<OperationClaim<IdentityKey>>> OperationClaimsShouldExist(
        IReadOnlyCollection<IdentityKey> ids,
        bool allowDeprecated,
        CancellationToken cancellationToken = default)
    {
        IdentityKey[] distinct = ids.Distinct().ToArray();
        // İzlenir: role / kullanıcıya bağlandıktan sonra navigation'lar dolu olsun (yanıttaki yetki adları).
        List<OperationClaim<IdentityKey>> claims = await _operationClaimRepository.Query()
            .Where(c => distinct.Contains(c.Id))
            .ToListAsync(cancellationToken);

        if (claims.Count != distinct.Length)
            throw new NotFoundException(OperationClaimsBusinessMessages.OperationClaimNotExists) { Code = OperationClaimsBusinessMessages.OperationClaimNotExistsCode };

        if (!allowDeprecated && claims.Any(c => c.IsDeprecated))
            throw new BusinessException(OperationClaimsBusinessMessages.OperationClaimDeprecated) { Code = OperationClaimsBusinessMessages.OperationClaimDeprecatedCode };

        return claims;
    }
}
