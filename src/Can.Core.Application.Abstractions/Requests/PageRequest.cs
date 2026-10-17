namespace Can.Core.Application.Requests;

/// <summary>
/// Sayfa numaralı liste isteği (nArchitecture'daki gibi). <see cref="Page"/> 0'dan başlar.
/// Controller'da <c>[FromQuery] PageRequest pageRequest</c> olarak alınır: <c>?Page=0&amp;PageSize=10</c>.
/// </summary>
public class PageRequest
{
    /// <summary>Bir sayfada en fazla kayıt sayısı.</summary>
    public const int MaxPageSize = 100;

    public int Page { get; set; }

    public int PageSize { get; set; } = 10;
}
