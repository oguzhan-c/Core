using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Can.Core.Persistence.Paging;

public static class QueryablePaginateExtensions
{
    /// <summary>
    /// Sorguyu sayfalar: önce toplam sayıyı, sonra yalnızca istenen sayfayı çeker.
    /// EF Core dışı (bellekteki) sorgularda senkron çalışır.
    /// </summary>
    /// <remarks>Tutarlı sayfalar için sorgunun sıralanmış olması gerekir.</remarks>
    public static async Task<IPaginate<T>> ToPaginateAsync<T>(
        this IQueryable<T> source,
        int index,
        int size,
        int from = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        PagingGuard.Validate(index, size, from);

        if (source.Provider is not IAsyncQueryProvider)
            return source.AsEnumerable().ToPaginate(index, size, from);

        int count = await source.CountAsync(cancellationToken).ConfigureAwait(false);

        List<T> items = count == 0
            ? []
            : await source.Skip((index - from) * size).Take(size).ToListAsync(cancellationToken).ConfigureAwait(false);

        return new Paginate<T>(items, index, size, count, from);
    }
}
