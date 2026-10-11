using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Settings.EntityFrameworkCore;

/// <summary>Bir ayarın bir düzeydeki kayıtlı değeri.</summary>
public sealed class SettingValue
{
    public const int NameMaxLength = 200;
    public const int TenantMaxLength = 64;

    public long Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Tenant kimliği; uygulama geneli için boş metin (NULL değil: benzersiz indeks her veritabanında çalışsın).</summary>
    public string TenantId { get; set; } = "";

    /// <summary>JSON değer (gizli ayarda şifreli).</summary>
    public string Value { get; set; } = "";

    public DateTime UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }
}

public static class SettingsModelBuilderExtensions
{
    /// <summary><c>SettingValues</c> tablosu: (ad, tenant) benzersiz.</summary>
    /// <example><code>modelBuilder.ApplyCanSettingsModel();</code></example>
    public static ModelBuilder ApplyCanSettingsModel(this ModelBuilder modelBuilder, string tableName = "SettingValues", string? schema = null)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<SettingValue>(b =>
        {
            b.ToTable(tableName, schema);
            b.HasKey(v => v.Id);
            b.Property(v => v.Name).HasMaxLength(SettingValue.NameMaxLength).IsRequired();
            b.Property(v => v.TenantId).HasMaxLength(SettingValue.TenantMaxLength).IsRequired();
            b.Property(v => v.Value).IsRequired();
            b.Property(v => v.UpdatedBy).HasMaxLength(64);
            b.HasIndex(v => new { v.TenantId, v.Name }).IsUnique();
        });
        return modelBuilder;
    }

    /// <summary>Değerleri <typeparamref name="TContext"/>'te saklar (<see cref="ApplyCanSettingsModel"/> çağrılmış olmalı).</summary>
    public static CanSettingsBuilder UseEntityFrameworkStore<TContext>(this CanSettingsBuilder builder)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.UseStore<EfSettingStore<TContext>>();
    }
}

/// <summary>EF Core deposu. Tenant filtresi uygulanmaz: tenant anahtarı açıkça verilir.</summary>
public sealed class EfSettingStore<TContext> : ISettingStore
    where TContext : DbContext
{
    private readonly TContext _context;
    private readonly TimeProvider _time;

    public EfSettingStore(TContext context, TimeProvider? timeProvider = null)
    {
        _context = context;
        _time = timeProvider ?? TimeProvider.System;
    }

    private DbSet<SettingValue> Values => _context.Set<SettingValue>();

    public Task<Dictionary<string, string>> GetAllAsync(string tenantKey, CancellationToken cancellationToken = default) =>
        Values.AsNoTracking().Where(v => v.TenantId == tenantKey).ToDictionaryAsync(v => v.Name, v => v.Value, StringComparer.Ordinal, cancellationToken);

    public async Task SetAsync(string name, string tenantKey, string value, string? userId, CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            SettingValue? row = await Values.FirstOrDefaultAsync(v => v.TenantId == tenantKey && v.Name == name, cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new SettingValue { Name = name, TenantId = tenantKey };
                Values.Add(row);
            }

            row.Value = value;
            row.UpdatedAt = _time.GetUtcNow().UtcDateTime;
            row.UpdatedBy = userId;
            try
            {
                await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (DbUpdateException) when (attempt == 1)
            {
                // aynı anda başka bir istek ekledi (benzersiz indeks): onu güncelle
                _context.Entry(row).State = EntityState.Detached;
            }
        }
    }

    public async Task RemoveAsync(string name, string tenantKey, CancellationToken cancellationToken = default) =>
        await Values.Where(v => v.TenantId == tenantKey && v.Name == name).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
}
