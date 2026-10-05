using System.Runtime.CompilerServices;

namespace Can.Core.Persistence.Context;

/// <summary>
/// <see cref="CanDbContext"/>'in eklediği global query filter'ların adları. EF Core 10'un isimli
/// filtreleri sayesinde biri kapatılırken diğeri çalışmaya devam eder:
/// <code>
/// db.Products.IgnoreQueryFilters([CanQueryFilters.SoftDelete])   // silinmişler dahil, tenant filtresi hâlâ açık
/// </code>
/// </summary>
public static class CanQueryFilters
{
    public const string SoftDelete = "Can.SoftDelete";

    public const string Tenant = "Can.Tenant";
}

/// <summary>
/// Tenant filtresinde, <c>object?</c> tipindeki aktif tenant'ı entity'nin TenantId tipine çevirir.
/// Tenant yoksa ya da tip uyuşmuyorsa default (Guid.Empty, 0, null) döner; böylece hiçbir kayıt eşleşmez.
/// </summary>
public static class TenantFilterValue<TTenantId>
{
    public static TTenantId? From(object? currentTenantId) => currentTenantId is TTenantId id ? id : default;
}

/// <summary>
/// <c>ISoftDeletable</c> bir entity'yi işaretlemek yerine KALICI olarak silmek için.
/// Repository'de <c>Delete(entity, permanent: true)</c> bunu kendisi yapar; doğrudan
/// <c>DbContext.Remove</c> kullanıyorsan önce <see cref="MarkPermanent"/> çağır.
/// </summary>
public static class PermanentDeletion
{
    private static readonly ConditionalWeakTable<object, object> Marks = new();
    private static readonly object Marker = new();

    public static void MarkPermanent(object entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        Marks.AddOrUpdate(entity, Marker);
    }

    public static bool IsMarkedPermanent(object entity) => Marks.TryGetValue(entity, out _);

    internal static void Unmark(object entity) => Marks.Remove(entity);
}
