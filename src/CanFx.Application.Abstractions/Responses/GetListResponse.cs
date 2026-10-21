namespace CanFx.Application.Responses;

/// <summary>
/// Sayfalı liste yanıtı (nArchitecture'daki <c>GetListResponse&lt;T&gt;</c>). Sayfa numarası (<see cref="Index"/>) 0'dan başlar.
/// Sayfalanmış sorgu sonucundan <c>paginate.ToGetListResponse()</c> ile oluşturulur.
/// </summary>
public class GetListResponse<T> : IResponse
{
    public IList<T> Items { get; set; } = [];

    /// <summary>Bu sayfanın numarası.</summary>
    public int Index { get; set; }

    /// <summary>Sayfa başına kayıt sayısı.</summary>
    public int Size { get; set; }

    /// <summary>Toplam kayıt sayısı.</summary>
    public int Count { get; set; }

    /// <summary>Toplam sayfa sayısı.</summary>
    public int Pages { get; set; }

    public bool HasPrevious { get; set; }

    public bool HasNext { get; set; }
}
