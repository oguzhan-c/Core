using CanFx.Application;
using CanFx.Application.Exceptions;
using CanFx.Application.Rules;
using CanFx.Domain.Exceptions;
using CanFx.Persistence.Repositories;
using CanFx.Security.Entities;
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Application.Features.Roles.Constants;

namespace CanAppTemplate.Application.Features.Roles.Rules;

/// <summary>
/// Rol kuralları: kayıtların varlığı ve tutarlılığı. Yetki YÜKSELTME kuralları (alt küme, kendini değiştirememe, sistem
/// rolleri, son yönetici) Core'daki <c>IAccessGrantGuard</c>'dadır; yetki kataloğu kuralları <c>OperationClaimBusinessRules</c>'ta.
/// </summary>
public sealed class RoleBusinessRules : BaseBusinessRules
{
    private readonly IRepository<Role<IdentityKey>, IdentityKey> _roleRepository;
    private readonly IRepository<UserRole<IdentityKey>, IdentityKey> _userRoleRepository;
    private readonly ICurrentTenant _currentTenant;

    public RoleBusinessRules(
        IRepository<Role<IdentityKey>, IdentityKey> roleRepository,
        IRepository<UserRole<IdentityKey>, IdentityKey> userRoleRepository,
        ICurrentTenant currentTenant)
    {
        _roleRepository = roleRepository;
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
    public async Task<Role<IdentityKey>> RoleIdShouldExistWhenSelected(IdentityKey id, CancellationToken cancellationToken = default)
    {
        Role<IdentityKey>? role = await _roleRepository.GetByIdAsync(id, include: IncludeOperationClaims, cancellationToken: cancellationToken);
        if (role is null)
            throw new NotFoundException(RolesBusinessMessages.RoleNotExists) { Code = RolesBusinessMessages.RoleNotExistsCode };
        return role;
    }

    public async Task RoleNameCanNotBeDuplicated(string name, IdentityKey? exceptId, CancellationToken cancellationToken = default)
    {
        string normalized = name.Trim().ToUpperInvariant();
        if (await _roleRepository.AnyAsync(r => r.NormalizedName == normalized && (exceptId == null || r.Id != exceptId), cancellationToken: cancellationToken))
            throw new BusinessException(RolesBusinessMessages.RoleNameExists) { Code = RolesBusinessMessages.RoleNameExistsCode };
    }

    /// <summary>Kullanıcısı olan rol silinmez (tüm tenant'lar dahil).</summary>
    public async Task RoleShouldNotBeInUse(IdentityKey roleId, CancellationToken cancellationToken = default)
    {
        if (await _userRoleRepository.AnyAsync(ur => ur.RoleId == roleId, cancellationToken: cancellationToken))
            throw new ConflictException(RolesBusinessMessages.RoleInUse) { Code = RolesBusinessMessages.RoleInUseCode };
    }

    /// <summary>Rolün etkin yetki adları (<see cref="IncludeOperationClaims"/> ile yüklenmiş olmalı).</summary>
    public static IReadOnlyList<string> PermissionNamesOf(Role<IdentityKey> role) =>
        role.OperationClaims
            .Where(rc => rc.OperationClaim is { IsDeprecated: false })
            .Select(rc => rc.OperationClaim!.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    public static IQueryable<Role<IdentityKey>> IncludeOperationClaims(IQueryable<Role<IdentityKey>> query) =>
        query.Include(r => r.OperationClaims).ThenInclude(rc => rc.OperationClaim);
}
