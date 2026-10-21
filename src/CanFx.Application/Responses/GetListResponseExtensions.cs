using CanFx.Persistence.Paging;

namespace CanFx.Application.Responses;

public static class GetListResponseExtensions
{
    /// <summary>Sayfalanmış sonucu yanıta çevirir.</summary>
    /// <example><code>IPaginate&lt;BrandListItemDto&gt; page = await query.ProjectTo&lt;BrandListItemDto&gt;(mapper).ToPaginateAsync(0, 10, ct);
    /// return page.ToGetListResponse();</code></example>
    public static GetListResponse<T> ToGetListResponse<T>(this IPaginate<T> paginate)
    {
        ArgumentNullException.ThrowIfNull(paginate);
        return new GetListResponse<T>
        {
            Items = [.. paginate.Items],
            Index = paginate.Index,
            Size = paginate.Size,
            Count = paginate.Count,
            Pages = paginate.Pages,
            HasPrevious = paginate.HasPrevious,
            HasNext = paginate.HasNext,
        };
    }

    /// <summary>Elemanları dönüştürerek yanıta çevirir: <c>brands.ToGetListResponse(b =&gt; mapper.Map&lt;BrandDto&gt;(b))</c>.</summary>
    public static GetListResponse<TResult> ToGetListResponse<TSource, TResult>(this IPaginate<TSource> paginate, Func<TSource, TResult> map)
    {
        ArgumentNullException.ThrowIfNull(paginate);
        ArgumentNullException.ThrowIfNull(map);
        return new GetListResponse<TResult>
        {
            Items = paginate.Items.Select(map).ToList(),
            Index = paginate.Index,
            Size = paginate.Size,
            Count = paginate.Count,
            Pages = paginate.Pages,
            HasPrevious = paginate.HasPrevious,
            HasNext = paginate.HasNext,
        };
    }
}
