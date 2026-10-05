namespace Can.Core.Domain.Entities;

/// <summary>
/// Tüm entity'ler için generic olmayan işaretleyici. Global filtre, interceptor gibi
/// Id tipini bilmeye gerek olmayan altyapı kodları bu arayüz üzerinden çalışır.
/// </summary>
public interface IEntity { }

/// <summary>
/// Kimliği <typeparamref name="TId"/> tipinde olan entity.
/// </summary>
/// <typeparam name="TId">
/// Id tipi. Kullanıcı seçer: <see cref="int"/>, <see cref="long"/>, <see cref="Guid"/>,
/// <see cref="string"/> ya da strongly-typed bir Id (ör. <c>record struct OrderId(Guid Value)</c>).
/// </typeparam>
public interface IEntity<out TId> : IEntity
    where TId : notnull
{
    TId Id { get; }
}
