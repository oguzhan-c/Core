using Can.Core.Domain.Results;
using Can.Core.Persistence.Dynamic;

namespace Northwind.Application.Common;

/// <summary>İstemcinin gönderdiği <see cref="DynamicQuery"/>'yi güvenle uygular.</summary>
internal static class DynamicSearch
{
    /// <summary>
    /// Filtre ve sıralamayı ekler. Sıralama verilmezse <paramref name="defaultSort"/> kullanılır (sayfalama tutarlı olsun).
    /// Geçersiz alan, operatör ya da değer doğrulama hatası (400) olarak döner.
    /// </summary>
    public static Result<IQueryable<T>> Apply<T>(IQueryable<T> query, DynamicQuery dynamicQuery, Func<IQueryable<T>, IOrderedQueryable<T>> defaultSort)
    {
        try
        {
            if (dynamicQuery.Filter is not null)
                query = query.ApplyFilter(dynamicQuery.Filter);

            return Result.Ok(dynamicQuery.Sort?.Any() == true ? query.ApplySort(dynamicQuery.Sort) : defaultSort(query));
        }
        catch (ArgumentException exception)
        {
            return Error.Validation("query.invalid", exception.Message, "query");
        }
    }
}
