using Can.Core.Domain.Results;
using Can.Core.Persistence.Dynamic;

namespace Northwind.Application.Common;

/// <summary>
/// İstemcinin gönderdiği <see cref="DynamicQuery"/>'yi güvenle uygular: yalnızca izin listesindeki alanlar kullanılabilir.
/// </summary>
internal static class DynamicSearch
{
    /// <summary>
    /// Filtre ve sıralamayı ekler. Sıralama verilmezse <paramref name="defaultSort"/> kullanılır (sayfalama tutarlı olsun).
    /// Geçersiz ya da izin verilmeyen alan, operatör veya değer doğrulama hatası (400) olarak döner.
    /// </summary>
    /// <typeparam name="T">Sorgulanan entity.</typeparam>
    /// <param name="query">Temel sorgu (tenant ve soft delete filtreleri zaten uygulanmış).</param>
    /// <param name="dynamicQuery">İstemcinin filtre ve sıralaması.</param>
    /// <param name="fields">İstemcinin kullanabileceği alanlar.</param>
    /// <param name="defaultSort">Sıralama verilmezse kullanılacak sıralama.</param>
    /// <returns>Sorgu ya da doğrulama hatası.</returns>
    public static Result<IQueryable<T>> Apply<T>(
        IQueryable<T> query,
        DynamicQuery dynamicQuery,
        DynamicQueryFields<T> fields,
        Func<IQueryable<T>, IOrderedQueryable<T>> defaultSort)
    {
        try
        {
            if (dynamicQuery.Filter is not null)
                query = query.ApplyFilter(dynamicQuery.Filter, fields);

            return Result.Ok(dynamicQuery.Sort?.Any() == true ? query.ApplySort(dynamicQuery.Sort, fields) : defaultSort(query));
        }
        catch (ArgumentException exception)
        {
            return Error.Validation("query.invalid", exception.Message, "query");
        }
    }
}
