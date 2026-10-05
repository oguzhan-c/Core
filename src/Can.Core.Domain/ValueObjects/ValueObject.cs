namespace Can.Core.Domain.ValueObjects;

/// <summary>
/// Değere göre karşılaştırılan, kimliği olmayan nesneler için temel sınıf (ör. Money, Address).
/// </summary>
/// <remarks>
/// Basit value object'ler için C# <c>record</c> da yeterlidir; bu sınıf, eşitliğe hangi
/// bileşenlerin katılacağını açıkça seçmek istediğinde kullanılır.
/// <code>
/// public sealed class Money(decimal amount, string currency) : ValueObject
/// {
///     public decimal Amount { get; } = amount;
///     public string Currency { get; } = currency;
///
///     protected override IEnumerable&lt;object?&gt; GetEqualityComponents()
///     {
///         yield return Amount;
///         yield return Currency;
///     }
/// }
/// </code>
/// </remarks>
public abstract class ValueObject : IEquatable<ValueObject>
{
    /// <summary>Eşitliğe katılan bileşenler, sırası önemlidir.</summary>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    public bool Equals(ValueObject? other)
    {
        if (other is null)
            return false;

        if (ReferenceEquals(this, other))
            return true;

        if (GetType() != other.GetType())
            return false;

        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    public override bool Equals(object? obj) => obj is ValueObject other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(GetType());

        foreach (object? component in GetEqualityComponents())
            hash.Add(component);

        return hash.ToHashCode();
    }

    public static bool operator ==(ValueObject? left, ValueObject? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}
