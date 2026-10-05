using System.Linq.Expressions;
using Can.Core.Domain.Entities;
using Can.Core.Persistence.Dynamic;
using Can.Core.Persistence.Paging;

namespace Can.Core.Persistence.Repositories;

/// <summary>
/// Entity'ler için genel amaçlı repository. Değişiklikleri KAYDETMEZ; kaydetme
/// <see cref="IUnitOfWork.SaveChangesAsync"/> ile tek seferde yapılır.
/// </summary>
/// <remarks>
/// <para>
/// <c>include</c> parametreleri EF Core'a bağımlı olmamak için <c>Func&lt;IQueryable&lt;T&gt;, IQueryable&lt;T&gt;&gt;</c>
/// tipindedir: <c>include: q =&gt; q.Include(p =&gt; p.Category)</c>.
/// </para>
/// <para>
/// Soft delete uygulanan (<c>ISoftDeletable</c>) entity'ler varsayılan olarak sorgulara gelmez;
/// <c>withDeleted: true</c> ile dahil edilir. Tenant filtresi ise her zaman uygulanır.
/// </para>
/// </remarks>
public interface IRepository<TEntity, TId>
    where TEntity : Entity<TId>
    where TId : notnull, IEquatable<TId>
{
    /// <summary>Özel sorgular ve <c>ProjectTo</c> için başlangıç noktası.</summary>
    IQueryable<TEntity> Query(bool withDeleted = false, bool enableTracking = true);

    Task<TEntity?> GetByIdAsync(
        TId id,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? include = null,
        bool withDeleted = false,
        bool enableTracking = true,
        CancellationToken cancellationToken = default);

    Task<TEntity?> GetAsync(
        Expression<Func<TEntity, bool>> predicate,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? include = null,
        bool withDeleted = false,
        bool enableTracking = true,
        CancellationToken cancellationToken = default);

    Task<IPaginate<TEntity>> GetListAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? orderBy = null,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? include = null,
        int index = 0,
        int size = 10,
        bool withDeleted = false,
        bool enableTracking = true,
        CancellationToken cancellationToken = default);

    /// <summary>İstemciden gelen <see cref="DynamicQuery"/> (filtre + sıralama) ile sayfalı liste.</summary>
    Task<IPaginate<TEntity>> GetListByDynamicAsync(
        DynamicQuery dynamicQuery,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? include = null,
        int index = 0,
        int size = 10,
        bool withDeleted = false,
        bool enableTracking = true,
        CancellationToken cancellationToken = default);

    Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        bool withDeleted = false,
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        bool withDeleted = false,
        CancellationToken cancellationToken = default);

    Task<TEntity> AddAsync(TEntity entity, CancellationToken cancellationToken = default);

    Task AddRangeAsync(IEnumerable<TEntity> entities, CancellationToken cancellationToken = default);

    void Update(TEntity entity);

    void UpdateRange(IEnumerable<TEntity> entities);

    /// <summary>
    /// Entity <c>ISoftDeletable</c> ise işaretlenir (soft delete); değilse ya da
    /// <paramref name="permanent"/> <see langword="true"/> ise kalıcı olarak silinir.
    /// </summary>
    void Delete(TEntity entity, bool permanent = false);

    void DeleteRange(IEnumerable<TEntity> entities, bool permanent = false);
}
