namespace CanFx.Application;

/// <summary>
/// O an çalışan işlemin adı (mediator isteği: <c>GetProductsQuery</c>). Mediator pipeline'ı kendiliğinden ayarlar;
/// altyapı bunu loglara ve SQL yorumlarına yazar ("bu yavaş sorgu nereden geldi?"). İç içe istekte en içteki görünür.
/// </summary>
public static class OperationContext
{
    private static readonly AsyncLocal<string?> CurrentName = new();

    /// <summary>Çalışan işlemin adı; yoksa <see langword="null"/>.</summary>
    public static string? Current => CurrentName.Value;

    /// <summary>İşlemi başlatır; dönen nesne dispose edilince önceki ad geri gelir.</summary>
    /// <param name="name">İşlemin adı.</param>
    public static IDisposable Begin(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string? previous = CurrentName.Value;
        CurrentName.Value = name;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            CurrentName.Value = previous;
        }
    }
}
