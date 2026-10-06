namespace Can.Core.Application;

/// <summary>
/// İsteği yapan kullanıcı. WebApi katmanı (ör. JWT claim'lerinden) doldurur; audit alanları
/// (<c>CreatedBy</c>, <c>UpdatedBy</c>, <c>DeletedBy</c>), yetkilendirme behavior'ı ve
/// "/me" gibi sorgular buradan okur.
/// </summary>
public interface ICurrentUser
{
    /// <summary>Kullanıcı kimliği; anonim isteklerde ya da arka plan işlerinde <see langword="null"/>.</summary>
    string? Id { get; }

    string? UserName { get; }

    string? Email { get; }

    IReadOnlyCollection<string> Roles { get; }

    /// <summary>Yetkiler (operation claim'ler); joker içerebilir (<c>"products.*"</c>, <c>"*"</c>).</summary>
    IReadOnlyCollection<string> Permissions => [];

    bool IsAuthenticated => Id is not null;

    bool IsInRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);

    /// <summary>Kullanıcının yetkileri <paramref name="permission"/>'ı kapsıyor mu (joker dahil).</summary>
    bool HasPermission(string permission) => PermissionMatcher.IsGranted(Permissions, permission);
}

/// <summary>Kullanıcı bilgisi olmayan ortamlar (testler, arka plan işleri) için varsayılan.</summary>
public sealed class NullCurrentUser : ICurrentUser
{
    public static readonly NullCurrentUser Instance = new();

    public string? Id => null;

    public string? UserName => null;

    public string? Email => null;

    public IReadOnlyCollection<string> Roles => [];
}
