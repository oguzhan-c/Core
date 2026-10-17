using Can.Core.Application;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Authorization;
using Can.Core.Security.Entities;
using Microsoft.EntityFrameworkCore;

namespace CanAppTemplate.Application.Features.OperationClaims.Queries.GetList;

/// <summary>
/// Yetki kataloğu. Yetkiler KODLA tanımlanır (<c>AppOperationClaims</c>) ve açılışta veritabanına eşitlenir; yönetim
/// ekranı yetki oluşturmaz / silmez, yalnızca rollere ve kullanıcılara dağıtır.
/// </summary>
public sealed class GetListOperationClaimQuery : IRequest<IReadOnlyList<OperationClaimDto>>, ISecuredRequest
{
    /// <summary>Koddan kaldırılmış (kullanımdan kalkmış) yetkiler de listelensin mi.</summary>
    public bool IncludeDeprecated { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyCollection<string> Permissions => [IdentityPermissions.RolesRead];
}

public sealed record OperationClaimDto(Guid Id, string Name, string? Description, bool IsDeprecated)
{
    public static OperationClaimDto From(OperationClaim<Guid> claim) => new(claim.Id, claim.Name, claim.Description, claim.IsDeprecated);
}

public sealed class GetListOperationClaimQueryHandler : IRequestHandler<GetListOperationClaimQuery, IReadOnlyList<OperationClaimDto>>
{
    private readonly IRepository<OperationClaim<Guid>, Guid> _operationClaimRepository;

    public GetListOperationClaimQueryHandler(IRepository<OperationClaim<Guid>, Guid> operationClaimRepository) =>
        _operationClaimRepository = operationClaimRepository;

    public async Task<IReadOnlyList<OperationClaimDto>> Handle(GetListOperationClaimQuery request, CancellationToken cancellationToken)
    {
        IQueryable<OperationClaim<Guid>> query = _operationClaimRepository.Query(enableTracking: false);
        if (!request.IncludeDeprecated)
            query = query.Where(c => !c.IsDeprecated);

        return await query
            .OrderBy(c => c.Name)
            .Select(c => new OperationClaimDto(c.Id, c.Name, c.Description, c.IsDeprecated))
            .ToListAsync(cancellationToken);
    }
}
