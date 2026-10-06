namespace Can.Core.Application.Behaviors;

/// <summary>
/// Başarısız Result'ı bir sarmalayıcıdan (transaction, önbellek) dışarı taşımak için: sarmalayıcı exception görünce
/// geri alır / kaydetmez, davranış da yakalayıp yanıtı olduğu gibi döndürür. Pipeline dışına hiç çıkmaz.
/// </summary>
internal sealed class FailedResultSignal : Exception
{
    public FailedResultSignal(object? response)
        : base("Başarısız sonuç (iç sinyal).")
    {
        Response = response;
    }

    public object? Response { get; }
}
