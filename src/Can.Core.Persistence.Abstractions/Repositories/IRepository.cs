using System.Linq.Expressions;
using Can.Core.Domain.Entities;
using Can.Core.Persistence.Dynamic;
using Can.Core.Persistence.Paging;
using Can.Core.Persistence.Specifications;

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

    /// <summary>
    /// İmleçli (keyset) sayfa: sayfa ne kadar derin olursa olsun aynı hızda (OFFSET yok, toplam sayı yok). Sonsuz kaydırma,
    /// mobil listeler ve büyük tablolar için. Sıralamanın sonuna Id kendiliğinden eklenir (benzersizlik).
    /// </summary>
    /// <param name="order">Sıralama; boşsa Id'ye göre artan.</param>
    /// <param name="cursor">İstemcinin gönderdiği imleç (<see cref="ICursorPage{T}.NextCursor"/> ya da
    /// <see cref="ICursorPage{T}.PreviousCursor"/>); ilk sayfa için <see langword="null"/>.</param>
    /// <param name="size">Sayfa boyutu.</param>
    /// <param name="predicate">Koşul.</param>
    /// <param name="include">İlişkili veriler.</param>
    /// <param name="withDeleted">Silinmişler dahil mi.</param>
    /// <param name="enableTracking">Değişiklik izleme açık mı.</param>
    /// <param name="cancellationToken">İptal belirteci.</param>
    /// <exception cref="Can.Core.Domain.Exceptions.BusinessException">İmleç geçersiz (<c>cursor_invalid</c>, 400).</exception>
    Task<ICursorPage<TEntity>> GetCursorPageAsync(
        CursorOrder<TEntity>? order = null,
        string? cursor = null,
        int size = 20,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? include = null,
        bool withDeleted = false,
        bool enableTracking = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// İstemciden gelen <see cref="DynamicQuery"/> (filtre + sıralama) ile sayfalı liste.
    /// </summary>
    /// <param name="dynamicQuery">Filtre ve sıralama.</param>
    /// <param name="predicate">Ek sabit koşul (ör. kullanıcının kendi kayıtları).</param>
    /// <param name="include">İlişkili veriler.</param>
    /// <param name="index">Sayfa (0'dan başlar).</param>
    /// <param name="size">Sayfa boyutu.</param>
    /// <param name="withDeleted">Silinmişler dahil mi.</param>
    /// <param name="enableTracking">Değişiklik izleme açık mı.</param>
    /// <param name="fields">İstemcinin kullanabileceği alanlar; boşsa yalnızca üst düzey property'ler. İstemci girdisinde verilmesi önerilir.</param>
    /// <param name="cancellationToken">İptal belirteci.</param>
    /// <returns>Sayfa.</returns>
    /// <exception cref="ArgumentException">Geçersiz ya da izin verilmeyen alan, operatör veya değer.</exception>
    Task<IPaginate<TEntity>> GetListByDynamicAsync(
        DynamicQuery dynamicQuery,
        Expression<Func<TEntity, bool>>? predicate = null,
        Func<IQueryable<TEntity>, IQueryable<TEntity>>? include = null,
        int index = 0,
        int size = 10,
        bool withDeleted = false,
        bool enableTracking = true,
        DynamicQueryFields<TEntity>? fields = null,
        CancellationToken cancellationToken = default);

    Task<bool> AnyAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        bool withDeleted = false,
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(
        Expression<Func<TEntity, bool>>? predicate = null,
        bool withDeleted = false,
        CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- Specification

    /// <summary>Specification'a uyan ilk kayıt (sıralama varsa ona göre).</summary>
    Task<TEntity?> FirstOrDefaultAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default);

    /// <summary>Specification'a uyan ilk kaydın projeksiyonu.</summary>
    Task<TResult?> FirstOrDefaultAsync<TResult>(ISpecification<TEntity, TResult> specification, CancellationToken cancellationToken = default);

    /// <summary>Specification'a uyan kayıtlar (specification'daki sayfa/Top uygulanır).</summary>
    Task<IReadOnlyList<TEntity>> ListAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default);

    /// <summary>Specification'a uyan kayıtların projeksiyonu; yalnızca seçilen kolonlar okunur.</summary>
    Task<IReadOnlyList<TResult>> ListAsync<TResult>(ISpecification<TEntity, TResult> specification, CancellationToken cancellationToken = default);

    /// <summary>Sayfalı liste; specification'daki sayfa yok sayılır. Sıralama yoksa Id'ye göre sıralanır.</summary>
    Task<IPaginate<TEntity>> PaginateAsync(
        ISpecification<TEntity> specification,
        int index = 0,
        int size = 10,
        CancellationToken cancellationToken = default);

    /// <summary>Projeksiyonlu sayfalı liste.</summary>
    Task<IPaginate<TResult>> PaginateAsync<TResult>(
        ISpecification<TEntity, TResult> specification,
        int index = 0,
        int size = 10,
        CancellationToken cancellationToken = default);

    /// <summary>Specification'ın koşuluna uyan kayıt sayısı (sayfa yok sayılır).</summary>
    Task<int> CountAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default);

    Task<bool> AnyAsync(ISpecification<TEntity> specification, CancellationToken cancellationToken = default);

    // ---------------------------------------------------------------- Yazma

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
