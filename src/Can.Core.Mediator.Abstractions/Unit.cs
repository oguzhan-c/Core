namespace Can.Core.Mediator;

/// <summary>
/// "Değer yok" anlamına gelen tip. Yanıt döndürmeyen istekler (<see cref="IRequest"/>)
/// içeride <see cref="IRequest{TResponse}"/> olarak <see cref="Unit"/> döndürür.
/// </summary>
public readonly struct Unit : IEquatable<Unit>
{
    public static readonly Unit Value = default;

    public bool Equals(Unit other) => true;

    public override bool Equals(object? obj) => obj is Unit;

    public override int GetHashCode() => 0;

    public override string ToString() => "()";

    public static bool operator ==(Unit left, Unit right) => true;

    public static bool operator !=(Unit left, Unit right) => false;
}
