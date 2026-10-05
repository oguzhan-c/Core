using Can.Core.Persistence.AuditTrail;
using Can.Core.Persistence.Context;
using Can.Core.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Northwind.Application.Common;
using Northwind.Domain.Identity;
using Northwind.Infrastructure.Persistence;

namespace Northwind.Infrastructure.Identity;

internal sealed class IdentityStore : IIdentityStore
{
    private readonly NorthwindDbContext _db;

    public IdentityStore(NorthwindDbContext db) => _db = db;

    public Task<AppUser?> FindUserInAnyTenantAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.Users
            .IgnoreQueryFilters([CanQueryFilters.Tenant]) // soft delete filtresi kalır
            .Include(u => u.UserRoles)
            .ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
}

internal sealed class AuditLogReader : IAuditLogReader
{
    private readonly NorthwindDbContext _db;

    public AuditLogReader(NorthwindDbContext db) => _db = db;

    public IQueryable<AuditLog> Query() => _db.Set<AuditLog>().AsNoTracking();
}

internal sealed class OutboxMonitor : IOutboxMonitor
{
    private readonly NorthwindDbContext _db;

    public OutboxMonitor(NorthwindDbContext db) => _db = db;

    public IQueryable<OutboxMessage> Query() => _db.Set<OutboxMessage>().AsNoTracking();

    public async Task<bool> RetryAsync(Guid id, string? tenantId, CancellationToken cancellationToken) =>
        await _db.Set<OutboxMessage>()
            .Where(m => m.Id == id && m.TenantId == tenantId && m.ProcessedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(m => m.Attempts, 0).SetProperty(m => m.LastError, (string?)null).SetProperty(m => m.LockedUntil, (DateTime?)null),
                cancellationToken
            ) > 0;
}
