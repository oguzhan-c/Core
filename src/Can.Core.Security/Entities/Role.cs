using Can.Core.Domain.Auditing;
using Can.Core.Domain.Entities;

namespace Can.Core.Security.Entities;

/// <summary>
/// Rol. Adları <c>ISecuredRequest.Roles</c> ile eşleşir; geniş roller ("Admin") ya da ince yetkiler
/// ("Product.Write") için kullanılabilir. JWT'ye <c>role</c> claim'i olarak yazılır.
/// </summary>
public class Role<TId> : AuditedEntity<TId>
    where TId : notnull, IEquatable<TId>
{
    protected Role()
    {
        Name = string.Empty;
        NormalizedName = string.Empty;
    }

    public Role(string name)
    {
        Name = string.Empty;
        NormalizedName = string.Empty;
        Rename(name);
    }

    public Role(TId id, string name)
        : base(id)
    {
        Name = string.Empty;
        NormalizedName = string.Empty;
        Rename(name);
    }

    public string Name { get; protected set; }

    public string NormalizedName { get; protected set; }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        NormalizedName = Name.ToUpperInvariant();
    }
}

/// <summary>Kullanıcı ↔ rol ilişkisi.</summary>
public class UserRole<TId> : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    protected UserRole()
    {
        UserId = default!;
        RoleId = default!;
    }

    public UserRole(TId userId, TId roleId)
    {
        UserId = userId;
        RoleId = roleId;
    }

    public TId UserId { get; protected set; }

    public TId RoleId { get; protected set; }

    public Role<TId>? Role { get; protected set; }
}
