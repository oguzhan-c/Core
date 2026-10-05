using System.Linq.Expressions;
using Can.Core.Application;
using Can.Core.Domain.Auditing;
using Can.Core.Domain.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Can.Core.Persistence.Context;

/// <summary>
/// Uygulama DbContext'lerinin temel sınıfı. Model oluşturulurken tüm entity'lere otomatik olarak:
/// <list type="bullet">
/// <item><see cref="CanQueryFilters.SoftDelete"/>: <c>ISoftDeletable</c> ise silinmişleri gizler.</item>
/// <item><see cref="CanQueryFilters.Tenant"/>: <c>IMultiTenant&lt;T&gt;</c> ise yalnızca aktif tenant'ın kayıtlarını getirir.</item>
/// </list>
/// </summary>
/// <example>
/// <code>
/// public sealed class AppDbContext(DbContextOptions&lt;AppDbContext&gt; options, ICurrentTenant currentTenant)
///     : CanDbContext(options, currentTenant)
/// {
///     public DbSet&lt;Product&gt; Products =&gt; Set&lt;Product&gt;();
///
///     protected override void ConfigureModel(ModelBuilder modelBuilder) =>
///         modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
/// }
/// </code>
/// </example>
/// <remarks>
/// Model yapılandırmasını <see cref="ConfigureModel"/> içinde yap. <c>OnModelCreating</c>'i override
/// edersen <c>base.OnModelCreating(modelBuilder)</c>'i EN SONDA çağır; filtreler o anda tüm entity'lere
/// eklenir. Aynı entity'ye kendi filtreni eklerken isimli filtre kullan (<c>HasQueryFilter("Benim", ...)</c>).
/// </remarks>
public abstract class CanDbContext : DbContext
{
    private readonly ICurrentTenant _currentTenant;

    protected CanDbContext(DbContextOptions options, ICurrentTenant? currentTenant = null)
        : base(options)
    {
        _currentTenant = currentTenant ?? NullCurrentTenant.Instance;
    }

    /// <summary>Tenant filtresinin her sorguda okuduğu değer (EF Core bunu SQL parametresine çevirir).</summary>
    public object? CurrentTenantId => _currentTenant.Id;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ConfigureModel(modelBuilder);
        ApplyCanQueryFilters(modelBuilder);
    }

    /// <summary>Entity yapılandırmalarını burada yap.</summary>
    protected virtual void ConfigureModel(ModelBuilder modelBuilder) { }

    /// <summary>Soft delete ve tenant filtrelerini modeldeki tüm kök entity'lere ekler.</summary>
    protected void ApplyCanQueryFilters(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            // Filtreler yalnızca hiyerarşinin kökünde tanımlanabilir; owned tiplerde tanımlanamaz.
            if (entityType.BaseType is not null || entityType.IsOwned() || entityType.HasSharedClrType)
                continue;

            Type clrType = entityType.ClrType;

            if (typeof(ISoftDeletable).IsAssignableFrom(clrType))
            {
                modelBuilder.Entity(clrType).HasQueryFilter(CanQueryFilters.SoftDelete, BuildSoftDeleteFilter(clrType));
            }

            Type? tenantIdType = GetTenantIdType(clrType);
            if (tenantIdType is not null)
            {
                modelBuilder.Entity(clrType).HasQueryFilter(CanQueryFilters.Tenant, BuildTenantFilter(clrType, tenantIdType));
            }
        }
    }

    /// <summary><c>e =&gt; !e.IsDeleted</c></summary>
    private static LambdaExpression BuildSoftDeleteFilter(Type clrType)
    {
        ParameterExpression entity = Expression.Parameter(clrType, "e");
        Expression isDeleted = Expression.Property(
            Expression.Convert(entity, typeof(ISoftDeletable)),
            nameof(ISoftDeletable.IsDeleted)
        );

        return Expression.Lambda(Expression.Not(isDeleted), entity);
    }

    /// <summary><c>e =&gt; e.TenantId == TenantFilterValue&lt;T&gt;.From(this.CurrentTenantId)</c></summary>
    private LambdaExpression BuildTenantFilter(Type clrType, Type tenantIdType)
    {
        ParameterExpression entity = Expression.Parameter(clrType, "e");

        Expression tenantId = Expression.Property(
            Expression.Convert(entity, typeof(IMultiTenant<>).MakeGenericType(tenantIdType)),
            nameof(IMultiTenant<int>.TenantId)
        );

        // "this" sabiti: EF Core sorgu anında bunu o anki DbContext örneğiyle değiştirir.
        Expression currentTenant = Expression.Call(
            typeof(TenantFilterValue<>).MakeGenericType(tenantIdType).GetMethod(nameof(TenantFilterValue<int>.From))!,
            Expression.Property(Expression.Constant(this), nameof(CurrentTenantId))
        );

        return Expression.Lambda(Expression.Equal(tenantId, currentTenant), entity);
    }

    internal static Type? GetTenantIdType(Type clrType) =>
        clrType
            .GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IMultiTenant<>))
            ?.GetGenericArguments()[0];
}
