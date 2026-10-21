#if (MultiTenancy)
using CanFx.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Application.Services.OperationClaims;
using CanAppTemplate.Application.Services.Repositories;
using CanAppTemplate.Persistence.Contexts;

namespace CanAppTemplate.Persistence.Repositories;

/// <summary>Tenant filtresini kapatarak okur (yalnızca yönetici/raporlama işlerinde kullan).</summary>
internal sealed class CrossTenantReader : ICrossTenantReader
{
    private readonly BaseDbContext _db;

    public CrossTenantReader(BaseDbContext db) => _db = db;

    public IQueryable<T> Query<T>(bool tracking = false)
        where T : class
    {
        IQueryable<T> query = _db.Set<T>().IgnoreQueryFilters([CanQueryFilters.Tenant]);
        return tracking ? query : query.AsNoTracking();
    }
}
#endif
