namespace Can.Core.Application.Rules;

/// <summary>
/// Bir özelliğe ait, veritabanı gibi dış kaynak gerektiren iş kurallarını toplayan sınıfların tabanı
/// (nArchitecture'daki gibi). <c>AddCanApplication</c> türeyen sınıfları otomatik olarak scoped kaydeder.
/// </summary>
/// <remarks>
/// Tek bir aggregate'in kendi tutarlılığıyla ilgili kurallar aggregate'in içinde kalmalı
/// (ör. <c>order.Confirm()</c>). Buraya birden fazla kaydı ilgilendiren kurallar gelir:
/// <code>
/// public sealed class ProductBusinessRules(IRepository&lt;Product, int&gt; products) : BaseBusinessRules
/// {
///     public async Task NameMustBeUnique(string name, CancellationToken ct)
///     {
///         if (await products.AnyAsync(p =&gt; p.Name == name, cancellationToken: ct))
///             throw new BusinessException($"'{name}' adında bir ürün zaten var.");
///     }
/// }
/// </code>
/// </remarks>
public abstract class BaseBusinessRules { }
