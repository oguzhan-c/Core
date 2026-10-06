using Can.Core.Domain.Auditing;
using Can.Core.Domain.Entities;

namespace Can.Core.Security.Entities;

/// <summary>
/// İnce taneli yetki (operation claim / permission): <c>"products.write"</c>, <c>"orders.cancel"</c>. Rollere ya da
/// doğrudan kullanıcılara verilir; JWT'ye <c>permission</c> claim'i olarak yazılır ve
/// <c>ISecuredRequest.Permissions</c> ile kontrol edilir.
/// </summary>
/// <remarks>
/// Ad küçük harfe çevrilir. Joker: <c>"products.*"</c> <c>products.</c> ile başlayan her yetkiyi, <c>"*"</c> hepsini kapsar.
/// </remarks>
public class OperationClaim<TId> : AuditedEntity<TId>
    where TId : notnull, IEquatable<TId>
{
    public const int NameMaxLength = 128;

    protected OperationClaim() => Name = string.Empty;

    public OperationClaim(string name, string? description = null)
    {
        Name = string.Empty;
        Rename(name);
        Description = description;
    }

    public OperationClaim(TId id, string name, string? description = null)
        : base(id)
    {
        Name = string.Empty;
        Rename(name);
        Description = description;
    }

    public string Name { get; protected set; }

    public string? Description { get; set; }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalized = name.Trim().ToLowerInvariant();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(normalized.Length, NameMaxLength, nameof(name));
        Name = normalized;
    }
}

/// <summary>Rol ↔ yetki ilişkisi.</summary>
public class RoleOperationClaim<TId> : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    protected RoleOperationClaim()
    {
        RoleId = default!;
        OperationClaimId = default!;
    }

    public RoleOperationClaim(TId roleId, TId operationClaimId)
    {
        RoleId = roleId;
        OperationClaimId = operationClaimId;
    }

    public TId RoleId { get; protected set; }

    public TId OperationClaimId { get; protected set; }

    public OperationClaim<TId>? OperationClaim { get; protected set; }
}

/// <summary>Kullanıcıya rolden bağımsız, doğrudan verilen yetki.</summary>
public class UserOperationClaim<TId> : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    protected UserOperationClaim()
    {
        UserId = default!;
        OperationClaimId = default!;
    }

    public UserOperationClaim(TId userId, TId operationClaimId)
    {
        UserId = userId;
        OperationClaimId = operationClaimId;
    }

    public TId UserId { get; protected set; }

    public TId OperationClaimId { get; protected set; }

    public OperationClaim<TId>? OperationClaim { get; protected set; }
}
