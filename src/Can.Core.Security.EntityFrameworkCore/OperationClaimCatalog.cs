using Can.Core.Security.Entities;
using Microsoft.EntityFrameworkCore;

namespace Can.Core.Security.EntityFrameworkCore;

public static class OperationClaimCatalogExtensions
{
    /// <summary>
    /// Yetki tablosunu koddaki kataloğa eşitler: eksikleri ekler, açıklamaları günceller, koddan kaldırılanları
    /// <see cref="OperationClaim{TId}.IsDeprecated"/> yapar (silmez: atamalar ve geçmiş korunur), geri ekleneni geri getirir.
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> çağırmaz.
    /// </summary>
    /// <remarks>Yetkiler kodla tanımlanır; yönetim ekranı yetki oluşturmaz / silmez, yalnızca rollere ve kullanıcılara dağıtır.</remarks>
    /// <returns>Ad → yetki (kullanımdan kalkmışlar dahil).</returns>
    public static async Task<Dictionary<string, OperationClaim<TId>>> SyncOperationClaimsAsync<TId>(
        this DbContext context,
        IEnumerable<(string Name, string Description)> catalog,
        Func<TId> newId,
        CancellationToken cancellationToken = default)
        where TId : notnull, IEquatable<TId>
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(newId);

        Dictionary<string, OperationClaim<TId>> existing = await context.Set<OperationClaim<TId>>()
            .ToDictionaryAsync(c => c.Name, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);

        var defined = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, string description) in catalog)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            string normalized = name.Trim().ToLowerInvariant();
            if (!defined.Add(normalized))
                continue;

            if (existing.TryGetValue(normalized, out OperationClaim<TId>? claim))
            {
                if (claim.IsDeprecated)
                    claim.Restore();
                if (!string.Equals(claim.Description, description, StringComparison.Ordinal))
                    claim.Description = description;
            }
            else
            {
                claim = new OperationClaim<TId>(newId(), normalized, description);
                context.Set<OperationClaim<TId>>().Add(claim);
                existing[normalized] = claim;
            }
        }

        foreach (OperationClaim<TId> claim in existing.Values)
        {
            if (!defined.Contains(claim.Name) && !claim.IsDeprecated)
                claim.Deprecate();
        }

        return existing;
    }
}
