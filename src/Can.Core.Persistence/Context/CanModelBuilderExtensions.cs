using Can.Core.Persistence.AuditTrail;
using Can.Core.Persistence.Inbox;
using Can.Core.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Can.Core.Persistence.Context;

public static class CanModelBuilderExtensions
{
    /// <summary>
    /// Inbox tablosunu modele ekler (broker'dan gelen event'lerin tekrarını ayıklar). Kullanmak için
    /// <c>services.AddCanInbox&lt;TContext&gt;()</c>.
    /// </summary>
    public static ModelBuilder AddCanInbox(this ModelBuilder modelBuilder, string tableName = "InboxMessages", string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<InboxMessage>(b =>
        {
            b.ToTable(tableName, schema);
            b.HasKey(m => new { m.Consumer, m.EventId });
            b.Property(m => m.Consumer).HasMaxLength(200);
            b.Property(m => m.EventName).HasMaxLength(512).IsRequired();
            b.HasIndex(m => m.ProcessedAt);
        });

        return modelBuilder;
    }

    /// <summary>
    /// Outbox tablosunu modele ekler. <c>IIntegrationEvent</c>'ler bu tabloya yazılır; yayınlamak için
    /// <c>services.AddCanOutbox&lt;TContext&gt;()</c> ile işlemciyi de kaydet.
    /// </summary>
    /// <example>
    /// <code>
    /// protected override void ConfigureModel(ModelBuilder modelBuilder)
    /// {
    ///     modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    ///     modelBuilder.AddCanOutbox();
    ///     modelBuilder.AddCanAuditTrail();
    /// }
    /// </code>
    /// </example>
    public static ModelBuilder AddCanOutbox(this ModelBuilder modelBuilder, string tableName = "OutboxMessages", string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable(tableName, schema);
            b.HasKey(m => m.Id);
            b.Property(m => m.Id).ValueGeneratedNever();
            b.Property(m => m.Type).HasMaxLength(512).IsRequired();
            b.Property(m => m.Payload).IsRequired();
            b.Property(m => m.TenantId).HasMaxLength(64);
            b.Property(m => m.LastError).HasMaxLength(2000);
            b.HasIndex(m => new { m.ProcessedAt, m.OccurredAt });
        });

        return modelBuilder;
    }

    /// <summary>
    /// Denetim kaydı (<see cref="AuditLog"/>) tablosunu modele ekler. Geçmişi tutulacak entity'leri <c>[Audited]</c> ile
    /// işaretle ya da <c>services.AddCanAuditTrail(o =&gt; o.AuditAllEntities = true)</c> kullan.
    /// </summary>
    public static ModelBuilder AddCanAuditTrail(this ModelBuilder modelBuilder, string tableName = "AuditLogs", string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<AuditLog>(b =>
        {
            b.ToTable(tableName, schema);
            b.HasKey(l => l.Id);
            b.Property(l => l.Id).ValueGeneratedNever();
            b.Property(l => l.EntityType).HasMaxLength(256).IsRequired();
            b.Property(l => l.EntityId).HasMaxLength(128).IsRequired();
            b.Property(l => l.Action).HasConversion<string>().HasMaxLength(16);
            b.Property(l => l.UserId).HasMaxLength(128);
            b.Property(l => l.TenantId).HasMaxLength(64);
            b.Property(l => l.TraceId).HasMaxLength(64);
            b.HasIndex(l => new { l.EntityType, l.EntityId });
            b.HasIndex(l => l.TenantId);
        });

        return modelBuilder;
    }
}
