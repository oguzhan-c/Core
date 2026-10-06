namespace Can.Core.Persistence.Specifications;

/// <summary>
/// Specification'ın koşul, sıralama ve sayfa kısmını bir <see cref="IQueryable{T}"/>'a uygular. Include, takip ve
/// soft delete veri erişim katmanının (ör. EF repository) işidir.
/// </summary>
public static class SpecificationEvaluator
{
    public static IQueryable<T> ApplyCriteria<T>(IQueryable<T> query, ISpecification<T> specification)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(specification);

        return specification.Criteria is null ? query : query.Where(specification.Criteria);
    }

    /// <summary>Sıralama varsa uygular; yoksa sorguyu olduğu gibi döner.</summary>
    public static IQueryable<T> ApplyOrdering<T>(IQueryable<T> query, ISpecification<T> specification)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(specification);

        for (int i = 0; i < specification.OrderClauses.Count; i++)
            query = specification.OrderClauses[i].Apply(query, first: i == 0);

        return query;
    }

    public static IQueryable<T> ApplyPaging<T>(IQueryable<T> query, ISpecification<T> specification)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(specification);

        if (specification.Skip is { } skip)
            query = query.Skip(skip);

        if (specification.Take is { } take)
            query = query.Take(take);

        return query;
    }

    /// <summary>Koşul + sıralama + (istenirse) sayfa.</summary>
    public static IQueryable<T> Apply<T>(IQueryable<T> query, ISpecification<T> specification, bool applyPaging = true)
    {
        query = ApplyOrdering(ApplyCriteria(query, specification), specification);
        return applyPaging ? ApplyPaging(query, specification) : query;
    }

    /// <summary>Bellekteki bir koleksiyona uygular (testler, önbellekteki listeler).</summary>
    public static IEnumerable<T> Evaluate<T>(this ISpecification<T> specification, IEnumerable<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Apply(source.AsQueryable(), specification);
    }
}
