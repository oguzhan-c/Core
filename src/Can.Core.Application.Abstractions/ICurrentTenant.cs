namespace Can.Core.Application;

/// <summary>
/// Aktif tenant. Multi-tenant uygulamalarda WebApi katmanı (header, subdomain, JWT claim ...) doldurur.
/// </summary>
/// <remarks>
/// <see cref="Id"/>'nin tipi, entity'lerdeki <c>IMultiTenant&lt;TTenantId&gt;.TenantId</c> ile aynı olmalıdır
/// (Guid, int, string ...). Tenant yoksa <see langword="null"/> döner; bu durumda tenant'a ait
/// kayıtlar sorgularda GÖRÜNMEZ (güvenli varsayılan).
/// </remarks>
public interface ICurrentTenant
{
    object? Id { get; }
}

/// <summary>Tenant kullanmayan uygulamalar için varsayılan.</summary>
public sealed class NullCurrentTenant : ICurrentTenant
{
    public static readonly NullCurrentTenant Instance = new();

    public object? Id => null;
}
