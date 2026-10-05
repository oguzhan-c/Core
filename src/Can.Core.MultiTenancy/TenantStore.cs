namespace Can.Core.MultiTenancy;

/// <summary>
/// Tenant tanımlarının kaynağı. Az sayıda tenant için <see cref="InMemoryTenantStore"/> (appsettings'ten)
/// yeterlidir; tenant'lar veritabanında tutuluyorsa bu arayüzü kendin uygula (sonuçları önbelleğe almayı unutma,
/// her istekte çağrılır).
/// </summary>
public interface ITenantStore
{
    /// <summary>Id ya da <see cref="TenantInfo.Identifier"/> ile arar (büyük/küçük harf duyarsız).</summary>
    Task<TenantInfo?> FindAsync(string idOrIdentifier, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TenantInfo>> GetAllAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryTenantStore : ITenantStore
{
    private readonly IReadOnlyList<TenantInfo> _tenants;

    public InMemoryTenantStore(IEnumerable<TenantInfo> tenants)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        _tenants = tenants.ToList();

        string? duplicate = _tenants
            .SelectMany(t => new[] { t.Id, t.Identifier })
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1)
            ?.Key;

        if (duplicate is not null)
            throw new InvalidOperationException($"Aynı tenant anahtarı birden fazla kez tanımlanmış: '{duplicate}'.");
    }

    public Task<TenantInfo?> FindAsync(string idOrIdentifier, CancellationToken cancellationToken = default)
    {
        TenantInfo? tenant = _tenants.FirstOrDefault(t =>
            string.Equals(t.Id, idOrIdentifier, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Identifier, idOrIdentifier, StringComparison.OrdinalIgnoreCase)
        );

        return Task.FromResult(tenant);
    }

    public Task<IReadOnlyList<TenantInfo>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_tenants);
}
