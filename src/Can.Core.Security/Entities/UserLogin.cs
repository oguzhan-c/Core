using Can.Core.Domain.Entities;

namespace Can.Core.Security.Entities;

/// <summary>
/// Kullanıcının dış sağlayıcı hesabı (Google, Microsoft, GitHub ...). <see cref="ProviderKey"/> sağlayıcının kullanıcıya
/// verdiği değişmez kimliktir (e-posta değil: e-posta değişebilir).
/// </summary>
public class UserLogin<TId> : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    public const int ProviderMaxLength = 64;
    public const int ProviderKeyMaxLength = 256;

    protected UserLogin()
    {
        UserId = default!;
        LoginProvider = string.Empty;
        ProviderKey = string.Empty;
    }

    public UserLogin(TId userId, string loginProvider, string providerKey, string? providerDisplayName, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(loginProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);

        UserId = userId;
        LoginProvider = loginProvider.Trim().ToLowerInvariant();
        ProviderKey = providerKey.Trim();
        ProviderDisplayName = providerDisplayName;
        CreatedAt = createdAt;
    }

    public TId UserId { get; protected set; }

    /// <summary>Sağlayıcının adı (küçük harf): <c>google</c>, <c>microsoft</c>, <c>github</c>.</summary>
    public string LoginProvider { get; protected set; }

    public string ProviderKey { get; protected set; }

    public string? ProviderDisplayName { get; protected set; }

    public DateTimeOffset CreatedAt { get; protected set; }
}
