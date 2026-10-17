using Can.Core.Application;
using Can.Core.Application.Exceptions;
using Can.Core.Application.Rules;
using Can.Core.Domain.Exceptions;
using Can.Core.Persistence.Repositories;
using Can.Core.Security.Entities;
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Application.Features.Roles.Constants;

namespace CanAppTemplate.Application.Features.Roles.Rules;

/// <summary>
/// Rol ve yetki kataloğu kuralları. Yetki YÜKSELTME kuralları (alt küme, kendini değiştirememe, sistem rolleri, son
/// yönetici) Core'daki <c>IAccessGrantGuard</c>'dadır; burada kayıtların varlığı ve tutarlılığı kontrol edilir.
/// </summary>
public sealed class RoleBusinessRules : BaseBusinessRules
{
    private readonly IRepository<Role<Guid>, Guid> _roleRepository;
    private readonly IRepository<OperationClaim<Guid>, Guid> _operationClaimRepository;
    private readonly IRepository<UserRole<Guid>, Guid> _userRoleRepository;
    private readonly ICurrentTenant _currentTenant;

    public RoleBusinessRules(
        IRepository<Role<Guid>, Guid> roleRepository,
        IRepository<OperationClaim<Guid>, Guid> operationClaimRepository,
        IRepository<UserRole<Guid>, Guid> userRoleRepository,
        ICurrentTenant currentTenant)
    {
        _roleRepository = roleRepository;
        _operationClaimRepository = operationClaimRepository;
        _userRoleRepository = userRoleRepository;
        _currentTenant = currentTenant;
    }

    /// <summary>Roller tüm tenant'larda ortaktır: tanımları yalnızca host (tenant'sız oturum) değiştirebilir.</summary>
    public void RoleDefinitionsShouldBeManagedFromHost()
    {
        if (_currentTenant.Id is not null)
            throw new ForbiddenException(RolesBusinessMessages.HostOnly) { Code = RolesBusinessMessages.HostOnlyCode };
    }

    /// <summary>Rolü yetkileriyle getirir (değişiklik izlenir); yoksa 404.</summary>
    public async Task<Role<Guid>> RoleIdShouldExistWhenSelected(Guid id, CancellationToken cancellationToken = default)
    {
        Role<Guid>? role = await _roleRepository.GetByIdAsync(id, include: IncludeOperationClaims, cancellationToken: cancellationToken);
        if (role is null)
            throw new NotFoundException(RolesBusinessMessages.RoleNotExists) { Code = RolesBusinessMessages.RoleNotExistsCode };
        return role;
    }

    public async Task RoleNameCanNotBeDuplicated(string name, Guid? exceptId, CancellationToken cancellationToken = default)
    {
        string normalized = name.Trim().ToUpperInvariant();
        if (await _roleRepository.AnyAsync(r => r.NormalizedName == normalized && (exceptId == null || r.Id != exceptId), cancellationToken: cancellationToken))
            throw new BusinessException(RolesBusinessMessages.RoleNameExists) { Code = RolesBusinessMessages.RoleNameExistsCode };
    }

    /// <summary>Kullanıcısı olan rol silinmez (tüm tenant'lar dahil).</summary>
    public async Task RoleShouldNotBeInUse(Guid roleId, CancellationToken cancellationToken = default)
    {
        if (await _userRoleRepository.AnyAsync(ur => ur.RoleId == roleId, cancellationToken: cancellationToken))
            throw new ConflictException(RolesBusinessMessages.RoleInUse) { Code = RolesBusinessMessages.RoleInUseCode };
    }

    /// <summary>Katalogdaki yetkileri getirir; biri yoksa 404, verilirken kullanımdan kalkmışsa 400.</summary>
    public async Task<IReadOnlyList<OperationClaim<Guid>>> OperationClaimsShouldExist(
        IReadOnlyCollection<Guid> ids,
        bool allowDeprecated,
        CancellationToken cancellationToken = default)
    {
        Guid[] distinct = ids.Distinct().ToArray();
        List<OperationClaim<Guid>> claims = await _operationClaimRepository.Query(enableTracking: false)
            .Where(c => distinct.Contains(c.Id))
            .ToListAsync(cancellationToken);

        if (claims.Count != distinct.Length)
            throw new NotFoundException(RolesBusinessMessages.OperationClaimNotExists) { Code = RolesBusinessMessages.OperationClaimNotExistsCode };

        if (!allowDeprecated && claims.Any(c => c.IsDeprecated))
            throw new BusinessException(RolesBusinessMessages.OperationClaimDeprecated) { Code = RolesBusinessMessages.OperationClaimDeprecatedCode };

        return claims;
    }

    /// <summary>Rolün etkin yetki adları (<see cref="IncludeOperationClaims"/> ile yüklenmiş olmalı).</summary>
    public static IReadOnlyList<string> PermissionNamesOf(Role<Guid> role) =>
        role.OperationClaims
            .Where(rc => rc.OperationClaim is { IsDeprecated: false })
            .Select(rc => rc.OperationClaim!.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public static IQueryable<Role<Guid>> IncludeOperationClaims(IQueryable<Role<Guid>> query) =>
        query.Include(r => r.OperationClaims).ThenInclude(rc => rc.OperationClaim);
}
