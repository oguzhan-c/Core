namespace CanFx.Application;

/// <summary>
/// Etiketle önbellek silen bir hedef: handler önbelleği (HybridCache), HTTP çıktı önbelleği... Bir etiket silindiğinde
/// kayıtlı hedeflerin hepsine iletilir; böylece <see cref="ICacheRemoverRequest.CacheTagsToRemove"/> ya da bir entity
/// değişikliği hem handler'ın hem HTTP yanıtının önbelleğini aynı anda temizler.
/// </summary>
/// <remarks>
/// Silme veri kaydedildikten sonra çalışır: uygulamalar hata fırlatmamalı (logla ve devam et). Aksi halde kaydedilmiş bir
/// işlem kullanıcıya hata olarak dönerdi.
/// </remarks>
public interface ICacheInvalidationTarget
{
    /// <summary>Bu etiketlere sahip kayıtları siler.</summary>
    /// <param name="tags">Silinecek etiketler (boş değil).</param>
    /// <param name="cancellationToken">İptal.</param>
    ValueTask InvalidateTagsAsync(IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default);
}

/// <summary>Ortak önbellek etiketleri.</summary>
public static class CacheTags
{
    /// <summary>Entity etiketlerinin öneki.</summary>
    public const string EntityPrefix = "entity:";

    /// <summary>
    /// Bir entity tipinin etiketi (<c>entity:Product</c>). Entity'ye bağlı önbellek kayıtlarına bu etiketi ver;
    /// <c>AddCanEntityCacheInvalidation()</c> açıksa o tipten bir kayıt eklendiğinde, değiştiğinde ya da silindiğinde
    /// kayıtlar kendiliğinden silinir.
    /// </summary>
    /// <example>
    /// <code>
    /// public IReadOnlyCollection&lt;string&gt; CacheTags =&gt; [CanFx.Application.CacheTags.Entity&lt;Product&gt;()];
    /// app.MapGet("/products", ...).WithResponseCache(ResponseCacheScope.Tenant, CacheTags.Entity&lt;Product&gt;());
    /// </code>
    /// </example>
    public static string Entity<TEntity>() => Entity(typeof(TEntity));

    /// <inheritdoc cref="Entity{TEntity}"/>
    /// <param name="entityType">Entity tipi; generic tiplerde ad <c>`1</c> eki olmadan kullanılır.</param>
    public static string Entity(Type entityType)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        return Entity(entityType.Name);
    }

    /// <summary>Ada göre entity etiketi (EF'in paylaşılan tipli entity'leri, ör. çoka-çok ara tablolar).</summary>
    /// <param name="entityName">Entity adı.</param>
    public static string Entity(string entityName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityName);
        int generic = entityName.IndexOf('`', StringComparison.Ordinal);
        return EntityPrefix + (generic < 0 ? entityName : entityName[..generic]);
    }
}

/// <summary><see cref="ICacheInvalidationTarget"/> yardımcıları.</summary>
public static class CacheInvalidationExtensions
{
    /// <summary>Etiketleri kayıtlı hedeflerin hepsine iletir.</summary>
    /// <param name="targets">Hedefler (DI'dan <c>IEnumerable&lt;ICacheInvalidationTarget&gt;</c>).</param>
    /// <param name="tags">Etiketler; boşsa hiçbir şey yapılmaz.</param>
    /// <param name="cancellationToken">İptal.</param>
    public static async ValueTask InvalidateTagsAsync(this IEnumerable<ICacheInvalidationTarget> targets, IReadOnlyCollection<string> tags, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count == 0)
            return;

        foreach (ICacheInvalidationTarget target in targets)
            await target.InvalidateTagsAsync(tags, cancellationToken).ConfigureAwait(false);
    }
}
