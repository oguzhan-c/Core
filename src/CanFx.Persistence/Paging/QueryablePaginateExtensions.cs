using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace CanFx.Persistence.Paging;

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

    /// <summary>
    /// İmleçli (keyset) sayfa. OFFSET yerine "son görülen kayıttan sonrası" sorgulanır: sayfa ne kadar derin olursa olsun
    /// aynı hızdadır, sayfalar arasında kayıt eklenip silinse de atlama ya da tekrar olmaz. Toplam sayı sorgulanmaz.
    /// </summary>
    /// <param name="source">Filtrelenmiş, SIRALANMAMIŞ sorgu (sıralamayı <paramref name="order"/> belirler).</param>
    /// <param name="order">Benzersiz sıralama (son anahtar genelde Id).</param>
    /// <param name="cursor">İstemcinin gönderdiği imleç; ilk sayfa için <see langword="null"/>.</param>
    /// <param name="size">Sayfa boyutu.</param>
    /// <param name="cancellationToken">İptal belirteci.</param>
    /// <exception cref="CanFx.Domain.Exceptions.BusinessException">İmleç geçersiz (<c>cursor_invalid</c>, 400).</exception>
    public static async Task<ICursorPage<T>> ToCursorPageAsync<T>(
        this IQueryable<T> source,
        CursorOrder<T> order,
        string? cursor,
        int size,
        CancellationToken cancellationToken = default)
    {
        CursorQuery<T> query = CursorPaging.Prepare(source, order, cursor, size);
        List<T> fetched = query.Query.Provider is IAsyncQueryProvider
            ? await query.Query.ToListAsync(cancellationToken).ConfigureAwait(false)
            : query.Query.ToList();
        return query.Complete(fetched);
    }
}
