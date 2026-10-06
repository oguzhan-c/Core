using System.Linq.Expressions;

namespace Can.Core.Persistence.Specifications;

/// <summary>
/// Adı konmuş, tekrar kullanılabilir bir sorgu: filtre + include + sıralama + sayfa (+ isteğe bağlı projeksiyon).
/// Sık kullanılan sorgular için; tek seferlik ya da istemciden gelen sorgular için <c>DynamicQuery</c> + repository yeterli.
/// </summary>
public interface ISpecification<T>
{
    /// <summary>Tüm <c>Where</c> koşullarının VE'lenmiş hâli; koşul yoksa <see langword="null"/>.</summary>
    Expression<Func<T, bool>>? Criteria { get; }

    /// <summary>Navigation include'ları (<c>p =&gt; p.Category</c>).</summary>
    IReadOnlyList<Expression<Func<T, object?>>> Includes { get; }

    /// <summary>İç içe include yolları (<c>"Lines.Product"</c>).</summary>
    IReadOnlyList<string> IncludePaths { get; }

    /// <summary>Sırasıyla uygulanan sıralamalar (ilki OrderBy, sonrakiler ThenBy).</summary>
    IReadOnlyList<OrderClause<T>> OrderClauses { get; }

    int? Skip { get; }

    int? Take { get; }

    /// <summary>Okunan entity'ler değişiklik takibine alınmasın (yalnızca okuma).</summary>
    bool AsNoTracking { get; }

    /// <summary>Soft delete ile silinmişler de gelsin.</summary>
    bool WithDeleted { get; }

    /// <summary>Entity bu kurala uyuyor mu? (bellekte; iş kurallarında ve testlerde kullanışlı)</summary>
    bool IsSatisfiedBy(T entity);
}

/// <summary>Sonucu <typeparamref name="TResult"/>'a projekte eden specification (yalnızca gereken kolonlar okunur).</summary>
public interface ISpecification<T, TResult> : ISpecification<T>
{
    Expression<Func<T, TResult>> Selector { get; }
}

/// <summary>Tek bir sıralama adımı.</summary>
public sealed class OrderClause<T>
{
    private readonly Func<IQueryable<T>, bool, IOrderedQueryable<T>> _apply;

    internal OrderClause(LambdaExpression keySelector, bool descending, Func<IQueryable<T>, bool, IOrderedQueryable<T>> apply)
    {
        KeySelector = keySelector;
        Descending = descending;
        _apply = apply;
    }

    public LambdaExpression KeySelector { get; }

    public bool Descending { get; }

    /// <param name="query">Sorgu.</param>
    /// <param name="first">İlk sıralama mı (OrderBy) yoksa sonraki mi (ThenBy).</param>
    public IOrderedQueryable<T> Apply(IQueryable<T> query, bool first) => _apply(query, first);
}

/// <summary>
/// Specification taban sınıfı. Kurallar yapıcıda tanımlanır:
/// </summary>
/// <example>
/// <code>
/// public sealed class ProductsInStockSpec : Specification&lt;Product&gt;
/// {
///     public ProductsInStockSpec(int? categoryId = null)
///     {
///         Where(p =&gt; p.UnitsInStock &gt; 0 &amp;&amp; !p.Discontinued);
///         if (categoryId is not null)
///             Where(p =&gt; p.CategoryId == categoryId);
///
///         Include(p =&gt; p.Category);
///         OrderBy(p =&gt; p.Name);
///         AsReadOnly();
///     }
/// }
///
/// IReadOnlyList&lt;Product&gt; products = await repository.ListAsync(new ProductsInStockSpec(3), ct);
/// IPaginate&lt;Product&gt; page = await repository.PaginateAsync(new ProductsInStockSpec(), index: 0, size: 20, ct);
/// bool ok = new ProductsInStockSpec().IsSatisfiedBy(product);
/// </code>
/// </example>
public abstract class Specification<T> : ISpecification<T>
{
    private readonly List<Expression<Func<T, object?>>> _includes = [];
    private readonly List<string> _includePaths = [];
    private readonly List<OrderClause<T>> _orderClauses = [];
    private Func<T, bool>? _compiled;

    public Expression<Func<T, bool>>? Criteria { get; private set; }

    public IReadOnlyList<Expression<Func<T, object?>>> Includes => _includes;

    public IReadOnlyList<string> IncludePaths => _includePaths;

    public IReadOnlyList<OrderClause<T>> OrderClauses => _orderClauses;

    public int? Skip { get; private set; }

    public int? Take { get; private set; }

    public bool AsNoTracking { get; private set; }

    public bool WithDeleted { get; private set; }

    public bool IsSatisfiedBy(T entity)
    {
        if (Criteria is null)
            return true;

        _compiled ??= Criteria.Compile();
        return _compiled(entity);
    }

    // ---------------------------------------------------------------- tanımlama

    /// <summary>Koşul ekler; birden fazla çağrı VE ile birleşir.</summary>
    protected void Where(Expression<Func<T, bool>> criteria)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        Criteria = Criteria is null ? criteria : Criteria.AndAlso(criteria);
        _compiled = null;
    }

    protected void Include(Expression<Func<T, object?>> navigation)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        _includes.Add(navigation);
    }

    /// <summary>İç içe include: <c>Include("Lines.Product")</c>.</summary>
    protected void Include(string navigationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(navigationPath);
        _includePaths.Add(navigationPath);
    }

    /// <summary>Artan sıralama; ilk çağrı OrderBy, sonrakiler ThenBy olur.</summary>
    protected void OrderBy<TKey>(Expression<Func<T, TKey>> keySelector) => AddOrder(keySelector, descending: false);

    /// <summary>Azalan sıralama; ilk çağrı OrderByDescending, sonrakiler ThenByDescending olur.</summary>
    protected void OrderByDescending<TKey>(Expression<Func<T, TKey>> keySelector) => AddOrder(keySelector, descending: true);

    /// <summary>Sayfa (0'dan başlar). <c>PaginateAsync</c> kendi sayfasını kullanır, bunu yok sayar.</summary>
    protected void Page(int index, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        Skip = index * size;
        Take = size;
    }

    /// <summary>İlk <paramref name="count"/> kayıt.</summary>
    protected void Top(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        Skip = null;
        Take = count;
    }

    /// <summary>Değişiklik takibi kapalı (yalnızca okuma; daha hızlı).</summary>
    protected void AsReadOnly() => AsNoTracking = true;

    /// <summary>Soft delete ile silinmiş kayıtlar da gelsin.</summary>
    protected void IncludeDeleted() => WithDeleted = true;

    private void AddOrder<TKey>(Expression<Func<T, TKey>> keySelector, bool descending)
    {
        ArgumentNullException.ThrowIfNull(keySelector);
        _orderClauses.Add(
            new OrderClause<T>(
                keySelector,
                descending,
                (query, first) =>
                    (first, descending) switch
                    {
                        (true, false) => query.OrderBy(keySelector),
                        (true, true) => query.OrderByDescending(keySelector),
                        (false, false) => ((IOrderedQueryable<T>)query).ThenBy(keySelector),
                        (false, true) => ((IOrderedQueryable<T>)query).ThenByDescending(keySelector),
                    }
            )
        );
    }

    // ---------------------------------------------------------------- birleştirme

    /// <summary>Yalnızca koşullardan oluşan specification (include/sıralama/sayfa yok).</summary>
    public static Specification<T> Create(Expression<Func<T, bool>> criteria) => new CriteriaSpecification<T>(criteria);

    /// <summary>İki specification'ın koşullarını VE'ler; include, sıralama ve diğer ayarlar soldakinden alınır.</summary>
    public Specification<T> And(ISpecification<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return CopyWith(Combine(Criteria, other.Criteria, (a, b) => a.AndAlso(b)));
    }

    /// <summary>İki specification'ın koşullarını VEYA'lar; include, sıralama ve diğer ayarlar soldakinden alınır.</summary>
    public Specification<T> Or(ISpecification<T> other)
    {
        ArgumentNullException.ThrowIfNull(other);

        // Koşulsuz taraf "hepsi" demektir; VEYA'nın sonucu da hepsi.
        Expression<Func<T, bool>>? criteria = Criteria is null || other.Criteria is null ? null : Criteria.OrElse(other.Criteria);
        return CopyWith(criteria);
    }

    /// <summary>Koşulun tersi; include, sıralama ve diğer ayarlar korunur.</summary>
    public Specification<T> Not() => CopyWith(Criteria?.Not() ?? (Expression<Func<T, bool>>)(_ => false));

    public static Specification<T> operator &(Specification<T> left, ISpecification<T> right) => left.And(right);

    public static Specification<T> operator |(Specification<T> left, ISpecification<T> right) => left.Or(right);

    public static Specification<T> operator !(Specification<T> spec) => spec.Not();

    private static Expression<Func<T, bool>>? Combine(
        Expression<Func<T, bool>>? left,
        Expression<Func<T, bool>>? right,
        Func<Expression<Func<T, bool>>, Expression<Func<T, bool>>, Expression<Func<T, bool>>> combine) =>
        (left, right) switch
        {
            (null, null) => null,
            (null, _) => right,
            (_, null) => left,
            _ => combine(left, right),
        };

    private CriteriaSpecification<T> CopyWith(Expression<Func<T, bool>>? criteria)
    {
        var copy = new CriteriaSpecification<T>(criteria);
        copy.CopySettingsFrom(this);
        return copy;
    }

    private void CopySettingsFrom(Specification<T> source)
    {
        _includes.AddRange(source._includes);
        _includePaths.AddRange(source._includePaths);
        _orderClauses.AddRange(source._orderClauses);
        Skip = source.Skip;
        Take = source.Take;
        AsNoTracking = source.AsNoTracking;
        WithDeleted = source.WithDeleted;
    }
}

/// <summary>Projeksiyonlu specification taban sınıfı: <c>Select(p =&gt; new ProductListItem(p.Id, p.Name))</c>.</summary>
public abstract class Specification<T, TResult> : Specification<T>, ISpecification<T, TResult>
{
    private Expression<Func<T, TResult>>? _selector;

    public Expression<Func<T, TResult>> Selector =>
        _selector ?? throw new InvalidOperationException($"{GetType().Name} için Select(...) çağrılmamış.");

    protected void Select(Expression<Func<T, TResult>> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        _selector = selector;
    }
}

internal sealed class CriteriaSpecification<T> : Specification<T>
{
    public CriteriaSpecification(Expression<Func<T, bool>>? criteria)
    {
        if (criteria is not null)
            Where(criteria);
    }
}
