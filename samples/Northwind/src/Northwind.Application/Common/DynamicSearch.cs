using Can.Core.Application.Exceptions;
using Can.Core.Persistence.Dynamic;

namespace Northwind.Application.Common;

/// <summary>İstemcinin gönderdiği <see cref="DynamicQuery"/>'yi güvenle uygular.</summary>
internal static class DynamicSearch
{
    /// <summary>
    /// Filtre ve sıralamayı ekler. Sıralama verilmezse <paramref name="defaultSort"/> kullanılır (sayfalama tutarlı olsun).
    /// Geçersiz alan, operatör ya da değer 400 (doğrulama hatası) olarak döner.
    /// </summary>
    public static IQueryable<T> Apply<T>(IQueryable<T> query, DynamicQuery dynamicQuery, Func<IQueryable<T>, IOrderedQueryable<T>> defaultSort)
    {
        try
        {
            if (dynamicQuery.Filter is not null)
                query = query.ApplyFilter(dynamicQuery.Filter);

            return dynamicQuery.Sort?.Any() == true ? query.ApplySort(dynamicQuery.Sort) : defaultSort(query);
        }
        catch (ArgumentException exception)
        {
            throw new ValidationException("query", exception.Message);
        }
    }
}
