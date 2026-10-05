namespace Can.Core.Domain.Entities;

/// <summary>
/// Tüm entity'lerin temel sınıfı.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="object.Equals(object)"/> ve <see cref="object.GetHashCode"/> bilinçli olarak
/// override EDİLMEZ; C#'ın varsayılan referans eşitliği korunur. Böylece EF Core'un
/// change tracker'ı, <see cref="HashSet{T}"/> gibi koleksiyonlar ve lazy-loading proxy'leri
/// sürprizsiz çalışır (kaydedilmemiş entity'lerin Id'si default olur, kayıttan sonra değişir).
/// </para>
/// <para>
/// Kimliğe göre karşılaştırma gerektiğinde açıkça <see cref="IsSameAs"/> çağrılır.
/// </para>
/// </remarks>
/// <typeparam name="TId">Id tipi (int, long, Guid, string, strongly-typed Id ...).</typeparam>
public abstract class Entity<TId> : IEntity<TId>
    where TId : notnull, IEquatable<TId>
{
    /// <summary>EF Core ve veritabanının ürettiği Id'ler için.</summary>
    protected Entity()
    {
        Id = default!;
    }

    /// <summary>Id'yi kendisi üreten entity'ler için (ör. <c>Guid.CreateVersion7()</c>).</summary>
    protected Entity(TId id)
    {
        Id = id;
    }

    public TId Id { get; protected set; }

    /// <summary>
    /// Entity henüz kalıcı bir Id almamışsa (Id == default) <see langword="true"/> döner.
    /// Örn. veritabanı identity kolonlu bir <see cref="int"/> Id, SaveChanges'tan önce 0'dır.
    /// </summary>
    public bool IsTransient() => EqualityComparer<TId>.Default.Equals(Id, default!);

    /// <summary>
    /// İki entity'nin aynı kaydı temsil edip etmediğini kimliğe göre kontrol eder.
    /// </summary>
    /// <remarks>
    /// Kurallar: aynı referans ise <see langword="true"/>; herhangi biri transient ise
    /// <see langword="false"/> (iki yeni kayıt "aynı" sayılmaz); gerçek tipleri farklıysa
    /// <see langword="false"/> (EF lazy-loading proxy'leri gerçek tipe indirgenir);
    /// aksi halde Id'ler karşılaştırılır.
    /// </remarks>
    public bool IsSameAs(Entity<TId>? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        if (IsTransient() || other.IsTransient())
            return false;

        if (GetUnproxiedType(this) != GetUnproxiedType(other))
            return false;

        return EqualityComparer<TId>.Default.Equals(Id, other.Id);
    }

    /// <summary>Hata ayıklamada okunaklı çıktı: <c>Order#42</c>.</summary>
    public override string ToString() => $"{GetUnproxiedType(this).Name}#{Id}";

    /// <summary>
    /// EF Core lazy-loading (Castle) proxy'lerinde gerçek entity tipini döndürür.
    /// </summary>
    private static Type GetUnproxiedType(object obj)
    {
        Type type = obj.GetType();
        return type.Namespace == "Castle.Proxies" && type.BaseType is not null ? type.BaseType : type;
    }
}
