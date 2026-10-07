using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Reporting.EntityFrameworkCore;

public static class ReportingModelBuilderExtensions
{
    /// <summary>
    /// <c>SavedReports</c> tablosu. Tanım JSON metin olarak saklanır; tenant + sahip ve tenant + paylaşım index'li.
    /// </summary>
    /// <example>
    /// <code>
    /// protected override void ConfigureModel(ModelBuilder modelBuilder)
    /// {
    ///     modelBuilder.ApplyCanReportingModel(schema: "reporting");
    /// }
    /// </code>
    /// </example>
    public static ModelBuilder ApplyCanReportingModel(this ModelBuilder modelBuilder, string tableName = "SavedReports", string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<SavedReport>(b =>
        {
            b.ToTable(tableName, schema);
            b.HasKey(r => r.Id);
            b.Property(r => r.Id).ValueGeneratedNever();
            b.Property(r => r.Name).HasMaxLength(SavedReport.NameMaxLength).IsRequired();
            b.Property(r => r.Description).HasMaxLength(SavedReport.DescriptionMaxLength);
            b.Property(r => r.DataSource).HasMaxLength(100).IsRequired();
            b.Property(r => r.Definition).IsRequired();
            b.Property(r => r.TenantId).HasMaxLength(64);
            b.Property(r => r.OwnerId).HasMaxLength(64).IsRequired();
            b.Property(r => r.OwnerName).HasMaxLength(200);
            b.HasIndex(r => new { r.TenantId, r.OwnerId });
            b.HasIndex(r => new { r.TenantId, r.IsShared });
        });

        return modelBuilder;
    }
}

/// <summary>EF Core deposu: DbContext'te <see cref="ReportingModelBuilderExtensions.ApplyCanReportingModel"/> çağrılmış olmalı.</summary>
public sealed class EfSavedReportStore<TContext> : ISavedReportStore
    where TContext : DbContext
{
    private readonly TContext _context;

    public EfSavedReportStore(TContext context) => _context = context;

    private DbSet<SavedReport> Reports => _context.Set<SavedReport>();

    public async Task<IReadOnlyList<SavedReport>> ListAsync(string? tenantId, string userId, CancellationToken cancellationToken = default) =>
        await Reports.AsNoTracking()
            .Where(r => r.TenantId == tenantId && (r.OwnerId == userId || r.IsShared))
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<SavedReport?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Reports.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

    public async Task AddAsync(SavedReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        Reports.Add(report);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(SavedReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (_context.Entry(report).State == EntityState.Detached)
            Reports.Update(report);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(SavedReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        Reports.Remove(report);
        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class ReportingEntityFrameworkBuilderExtensions
{
    /// <summary>Kaydedilmiş raporları <typeparamref name="TContext"/>'te saklar (bellek içi deponun yerine).</summary>
    public static CanReportingBuilder UseEntityFrameworkStore<TContext>(this CanReportingBuilder builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.RemoveAll<ISavedReportStore>();
        builder.Services.AddScoped<ISavedReportStore, EfSavedReportStore<TContext>>();
        return builder;
    }
}
