#if (MultiTenancy)
using Can.Core.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Application.Common;

namespace CanAppTemplate.Persistence.Services;

/// <summary>Tenant filtresini kapatarak okur (yalnızca yönetici/raporlama işlerinde kullan).</summary>
internal sealed class CrossTenantReader : ICrossTenantReader
{
    private readonly AppDbContext _db;

    public CrossTenantReader(AppDbContext db) => _db = db;

    public IQueryable<T> Query<T>(bool tracking = false)
        where T : class
    {
        IQueryable<T> query = _db.Set<T>().IgnoreQueryFilters([CanQueryFilters.Tenant]);
        return tracking ? query : query.AsNoTracking();
    }
}
#endif
