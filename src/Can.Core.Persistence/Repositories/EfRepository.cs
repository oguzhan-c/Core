using System.Linq.Expressions;
using Can.Core.Domain.Auditing;
using Can.Core.Domain.Entities;
using Can.Core.Persistence.Context;
using Can.Core.Persistence.Dynamic;
using Can.Core.Persistence.Paging;
using Microsoft.EntityFrameworkCore;

namespace Can.Core.Persistence.Repositories;

/// <summary>
/// <see cref="IRepository{TEntity, TId}"/>'nin EF Core implementasyonu. Hiçbir metot
/// <c>SaveChanges</c> çağırmaz; kaydetme <see cref="IUnitOfWork"/>'ün işidir.
/// </summary>
/// <remarks>
/// Özel sorgular gereken entity'ler için türetip genişletebilirsin:
/// <code>
/// public sealed class ProductRepository(DbContext context) : EfRepository&lt;Product, int&gt;(context), IProductRepository
/// {
///     public Task&lt;bool&gt; IsNameTakenAsync(string name, CancellationToken ct) =&gt; Query().AnyAsync(p =&gt; p.Name == name, ct);
/// }
/// </code>
/// </remarks>
public class EfRepository<TEntity, TId> : IRepository<TEntity, TId>
    where TEntity : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    public EfRepository(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
    }

    protected DbContext Context { get; }

    protected DbSet<TEntity> Set => Context.Set<TEntity>();

    // ---------------------------------------------------------------- Okuma

    public virtual IQueryable<TEntity> Query(bool withDeleted = false, bool enableTracking = true)
    {
        IQueryable<TEntity> query = Set;

        if (withDeleted)
            query = query.IgnoreQueryFilters([CanQueryFilters.SoftDelete]);

        if (!enableTracking)
            query = query.AsNoTracking();

        return query;
    }

    public virtual Task<TEntity?> GetByIdAsync(
        TId id,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? include = null,
        bool withDeleted = false,
        bool enableTracking = true,
        CancellationToken cancellationToken = default) =>
        GetAsync(IdEquals(id), include, withDeleted, enableTracking, cancellationToken);

    public virtual async Task<TEntity?> GetAsync(
        Expression<Func<TEntity, bool>> predicate,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? include = null,
        bool withDeleted = false,
        bool enableTracking = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        IQueryable<TEntity> query = Query(withDeleted, enableTracking);
        if (include is not null)
            query = include(query);

        return await query.FirstOrDefaultAsync(predicate, cancellationToken).ConfigureAwait(false);
    }

    public virtual Task<IPaginate<TEntity>> GetListAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? include = null,
        int index = 0,
        int size = 10,
        bool withDeleted = false,
        bool enableTracking = true,
        CancellationToken cancellationToken = default)
    {
        IQueryable<TEntity> query = Query(withDeleted, enableTracking);

        if (include is not null)
            query = include(query);

        if (predicate is not null)
            query = query.Where(predicate);

        // Sıralama verilmezse sayfaların tutarlı olması için Id'ye göre sırala.
        query = orderBy is not null ? orderBy(query) : query.OrderBy(e => e.Id);

        return query.ToPaginateAsync(index, size, cancellationToken: cancellationToken);
    }

    public virtual Task<IPaginate<TEntity>> GetListByDynamicAsync(
        DynamicQuery dynamicQuery,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? include = null,
        int index = 0,
        int size = 10,
        bool withDeleted = false,
        bool enableTracking = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dynamicQuery);

        IQueryable<TEntity> query = Query(withDeleted, enableTracking);

        if (include is not null)
            query = include(query);

        if (predicate is not null)
            query = query.Where(predicate);

        query = query.ToDynamic(dynamicQuery);

        if (dynamicQuery.Sort is null || !dynamicQuery.Sort.Any())
            query = query.OrderBy(e => e.Id);

        return query.ToPaginateAsync(index, size, cancellationToken: cancellationToken);
    }

    public virtual Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        bool withDeleted = false,
        CancellationToken cancellationToken = default)
    {
        IQueryable<TEntity> query = Query(withDeleted, enableTracking: false);
        return predicate is null ? query.AnyAsync(cancellationToken) : query.AnyAsync(predicate, cancellationToken);
    }

    public virtual Task<int> CountAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        bool withDeleted = false,
        CancellationToken cancellationToken = default)
    {
        IQueryable<TEntity> query = Query(withDeleted, enableTracking: false);
        return predicate is null ? query.CountAsync(cancellationToken) : query.CountAsync(predicate, cancellationToken);
    }

    // ---------------------------------------------------------------- Yazma (kaydetmez)

    public virtual async Task<TEntity> AddAsync(TEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await Set.AddAsync(entity, cancellationToken).ConfigureAwait(false);
        return entity;
    }

    public virtual Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);
        return Set.AddRangeAsync(entities, cancellationToken);
    }

    /// <summary>
    /// Takip edilen (bu DbContext'ten okunmuş) entity'ler için gerek yoktur; değişiklikler zaten izlenir.
    /// Takip edilmeyen (ör. <c>AsNoTracking</c> ile okunmuş) entity'yi güncellemek için kullan.
    /// </summary>
    public virtual void Update(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (Context.Entry(entity).State == EntityState.Detached)
            Set.Update(entity);
    }

    public virtual void UpdateRange(IEnumerable<TEntity> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);

        foreach (TEntity entity in entities)
            Update(entity);
    }

    public virtual void Delete(TEntity entity, bool permanent = false)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (!permanent && entity is ISoftDeletable)
        {
            // Remove yerine işaretleme: EF'in bağlı kayıtları cascade silmesini tetiklemez.
            if (Context.Entry(entity).State == EntityState.Detached)
                Set.Attach(entity);

            Context.Entry(entity).Property(nameof(ISoftDeletable.IsDeleted)).CurrentValue = true;
            return;
        }

        if (entity is ISoftDeletable)
            PermanentDeletion.MarkPermanent(entity);

        Set.Remove(entity);
    }

    public virtual void DeleteRange(IEnumerable<TEntity> entities, bool permanent = false)
    {
        ArgumentNullException.ThrowIfNull(entities);

        foreach (TEntity entity in entities)
            Delete(entity, permanent);
    }

    // ----------------------------------------------------------------

    /// <summary><c>e =&gt; e.Id == id</c> (id parametre olarak gönderilir).</summary>
    private static Expression<Func<TEntity, bool>> IdEquals(TId id)
    {
        ParameterExpression entity = Expression.Parameter(typeof(TEntity), "e");
        Expression<Func<TId>> idAccessor = () => id;

        return Expression.Lambda<Func<TEntity, bool>>(
            Expression.Equal(Expression.Property(entity, nameof(Entity<TId>.Id)), idAccessor.Body),
            entity
        );
    }
}
