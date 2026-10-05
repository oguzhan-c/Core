namespace Can.Core.Mapping;

public static class QueryableExtensions
{
    /// <summary>
    /// Sorguyu DTO'ya projekte eder; EF Core yalnızca gereken kolonları seçer.
    /// <code>
    /// var page = await db.Products
    ///     .Where(p => p.Price > 100)
    ///     .ProjectTo&lt;ProductDto&gt;(mapper)
    ///     .ToListAsync(ct);
    /// </code>
    /// </summary>
    public static IQueryable<TDestination> ProjectTo<TDestination>(this IQueryable source, IMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);
        return mapper.ProjectTo<TDestination>(source);
    }
}
