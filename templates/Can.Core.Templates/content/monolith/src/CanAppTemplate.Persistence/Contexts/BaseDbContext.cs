using Can.Core.Application;
using Can.Core.Persistence.Context;
#if (AuditTrail)
using Can.Core.Persistence.AuditTrail;
#endif
#if (EventBus)
using Can.Core.Persistence.Outbox;
#endif
using Can.Core.Persistence.RequestLogging;
#if (Reporting)
using Can.Core.Reporting.EntityFrameworkCore;
#endif
using Can.Core.Security;
using Can.Core.Security.Entities;
using Can.Core.Security.EntityFrameworkCore;
#if (Settings)
using Can.Core.Settings.EntityFrameworkCore;
#endif
using Microsoft.EntityFrameworkCore;
using CanAppTemplate.Domain.Constants;
using CanAppTemplate.Domain.Entities;

namespace CanAppTemplate.Persistence.Contexts;

/// <summary>
/// Uygulamanın DbContext'i. <see cref="CanDbContext"/> sayesinde soft delete filtresi (<c>ISoftDeletable</c>) ve
/// tenant filtresi (<c>IMultiTenant&lt;T&gt;</c>) ilgili tüm entity'lere kendiliğinden uygulanır.
/// </summary>
public sealed class BaseDbContext : CanDbContext
{
    private readonly ISecretProtector? _secretProtector;

    public BaseDbContext(DbContextOptions<BaseDbContext> options, ICurrentTenant currentTenant, ISecretProtector? secretProtector = null)
        : base(options, currentTenant)
    {
        _secretProtector = secretProtector;
    }

    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Role<IdentityKey>> Roles => Set<Role<IdentityKey>>();
    public DbSet<OperationClaim<IdentityKey>> OperationClaims => Set<OperationClaim<IdentityKey>>();
    // can-gen:dbsets

    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        // Kimlik tabloları: kullanıcı, rol, yetki, refresh token (oturum), 2FA, passkey, kurtarma kodu, kayıtlı cihaz.
        // TOTP anahtarları Data Protection ile şifreli saklanır.
        modelBuilder.ApplyCanSecurityModel<AppUser, IdentityKey>(o =>
        {
            o.Schema = Schemas.Identity;
            o.SecretProtector = _secretProtector;
        });
        modelBuilder.ApplyCanSecurityEventLog(o => o.Schema = Schemas.Identity);

        // Entity yapılandırmaları (Configurations klasöründeki IEntityTypeConfiguration sınıfları).
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BaseDbContext).Assembly);

        // Domain kimlikleri kodda üretilir (Guid v7). EF Core Guid anahtarını varsayılan olarak "veritabanı üretir" sayar ve
        // var olan bir aggregate'e eklenen yeni alt kaydı anahtarı dolu diye UPDATE etmeye çalışır
        // (0 satır → DbUpdateConcurrencyException). Bu yüzden anahtar hiçbir zaman üretilmez.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => !t.IsOwned() && t.ClrType.Namespace?.StartsWith("CanAppTemplate.Domain", StringComparison.Ordinal) == true))
        {
            if (entityType.FindPrimaryKey()?.Properties is [{ ClrType: var keyType, Name: "Id" } key] && keyType == typeof(Guid))
                key.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
        }

        // Altyapı tabloları.
#if (EventBus)
        modelBuilder.AddCanOutbox(schema: Schemas.Infrastructure);
        modelBuilder.AddCanInbox(schema: Schemas.Infrastructure);
#endif
#if (AuditTrail)
        modelBuilder.AddCanAuditTrail(schema: Schemas.Infrastructure);
#endif
        modelBuilder.AddCanRequestActivityLog(schema: Schemas.Infrastructure);
#if (Reporting)
        modelBuilder.ApplyCanReportingModel(schema: Schemas.Infrastructure);
#endif
#if (Settings)
        modelBuilder.ApplyCanSettingsModel(schema: Schemas.Infrastructure);
#endif

        // TODO(can-gen): Eşzamanlı güncellemeye açık entity'lere (stok, bakiye ...) satır sürümü ekle, ör. PostgreSQL:
        //     modelBuilder.Entity<Product>().Property<uint>("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        // SQL Server: entity'ye [Timestamp] byte[] RowVersion.
    }
}

internal static class Schemas
{
    public const string Identity = "identity";
    public const string Infrastructure = "infra";

    /// <summary>Uygulama tabloları. TODO(can-gen): Modül başına ayrı şema istiyorsan buraya ekle.</summary>
    public const string App = "app";
}
