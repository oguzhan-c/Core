using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Can.Core.Persistence.HealthChecks;

/// <summary>Veritabanına bağlanılabiliyor mu (<c>Database.CanConnectAsync</c>; ortak veritabanı).</summary>
public sealed class DbContextHealthCheck<TContext> : IHealthCheck
    where TContext : DbContext
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DbContextHealthCheck(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await using AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();
        TContext db = scope.ServiceProvider.GetRequiredService<TContext>();

        return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
            ? HealthCheckResult.Healthy()
            : new HealthCheckResult(context.Registration.FailureStatus, "Veritabanına bağlanılamadı.");
    }
}

public static class PersistenceHealthCheckExtensions
{
    /// <summary>Veritabanı bağlantı kontrolü ekler (varsayılan etiket: <c>ready</c>).</summary>
    public static IHealthChecksBuilder AddCanDbContextCheck<TContext>(
        this IHealthChecksBuilder builder,
        string name = "database",
        HealthStatus failureStatus = HealthStatus.Unhealthy,
        params string[] tags)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<DbContextHealthCheck<TContext>>(name, failureStatus, tags.Length > 0 ? tags : new[] { "ready" });
    }
}
