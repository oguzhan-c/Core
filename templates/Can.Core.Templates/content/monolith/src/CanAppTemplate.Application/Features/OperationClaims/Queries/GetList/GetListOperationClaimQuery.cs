using Can.Core.Application;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Application.Features.OperationClaims.Constants;

namespace CanAppTemplate.Application.Features.OperationClaims.Queries.GetList;

/// <summary>
/// Yetki kataloğu. Yetkiler KODLA tanımlanır (<c>AppOperationClaims</c>) ve açılışta veritabanına eşitlenir; yönetim
/// ekranı yetki oluşturmaz / silmez, yalnızca rollere ve kullanıcılara dağıtır.
/// </summary>
public sealed class GetListOperationClaimQuery : IRequest<IReadOnlyList<GetListOperationClaimListItemDto>>, ISecuredRequest
{
    /// <summary>Koddan kaldırılmış (kullanımdan kalkmış) yetkiler de listelensin mi.</summary>
    public bool IncludeDeprecated { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [OperationClaimsOperationClaims.Read];
}

public sealed class GetListOperationClaimQueryHandler : IRequestHandler<GetListOperationClaimQuery, IReadOnlyList<GetListOperationClaimListItemDto>>
{
    private readonly IRepository<OperationClaim<IdentityKey>, IdentityKey> _operationClaimRepository;

    public GetListOperationClaimQueryHandler(IRepository<OperationClaim<IdentityKey>, IdentityKey> operationClaimRepository) =>
        _operationClaimRepository = operationClaimRepository;

    public async Task<IReadOnlyList<GetListOperationClaimListItemDto>> Handle(GetListOperationClaimQuery request, CancellationToken cancellationToken)
    {
        IQueryable<OperationClaim<IdentityKey>> query = _operationClaimRepository.Query(enableTracking: false);
        if (!request.IncludeDeprecated)
            query = query.Where(c => !c.IsDeprecated);

        return await query
            .OrderBy(c => c.Name)
            .Select(c => new GetListOperationClaimListItemDto(c.Id, c.Name, c.Description, c.IsDeprecated))
            .ToListAsync(cancellationToken);
    }
}
