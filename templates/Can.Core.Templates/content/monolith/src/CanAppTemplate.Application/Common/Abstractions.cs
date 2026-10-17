#if (MultiTenancy)
namespace CanAppTemplate.Application.Common;

/// <summary>
/// Tenant'a ait kayıtları tenant filtresi OLMADAN okur (ör. host yönetim paneli tüm tenant'ların verisini görür).
/// Persistence uygular (<c>IgnoreQueryFilters([CanQueryFilters.Tenant])</c>).
/// </summary>
/// <remarks>
/// Yalnızca gerçekten tenant'lar arası okuma gereken yerlerde kullan ve sorguyu daima daralt. Tenant içindeki ekranlar
/// normal repository'yi kullanır: tenant filtresi kullanıcının yalnızca kendi tenant'ının verisini görmesini sağlar.
/// </remarks>
public interface ICrossTenantReader
{
    /// <param name="tracking">Değiştirilecekse <see langword="true"/>.</param>
    IQueryable<T> Query<T>(bool tracking = false)
        where T : class;
}
#endif
