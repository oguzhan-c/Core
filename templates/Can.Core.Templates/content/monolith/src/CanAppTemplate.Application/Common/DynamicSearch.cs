using Can.Core.Domain.Results;
using Can.Core.Persistence.Dynamic;

namespace CanAppTemplate.Application.Common;

/// <summary>
/// İstemcinin gönderdiği <see cref="DynamicQuery"/>'yi güvenle uygular: yalnızca izin listesindeki alanlar kullanılabilir.
/// Yönetim tablolarında (filtre + sıralama + sayfa numarası) kullanılır.
/// </summary>
internal static class DynamicSearch
{
    public static Result<IQueryable<T>> Apply<T>(
        IQueryable<T> query,
        DynamicQuery? dynamicQuery,
        DynamicQueryFields<T> fields,
        Func<IQueryable<T>, IOrderedQueryable<T>> defaultSort)
    {
        try
        {
            if (dynamicQuery?.Filter is not null)
                query = query.ApplyFilter(dynamicQuery.Filter, fields);

            return Result.Ok(dynamicQuery?.Sort?.Any() == true ? query.ApplySort(dynamicQuery.Sort, fields) : defaultSort(query));
        }
        catch (ArgumentException exception)
        {
            return Error.Validation("query.invalid", exception.Message, "query");
        }
    }
}
