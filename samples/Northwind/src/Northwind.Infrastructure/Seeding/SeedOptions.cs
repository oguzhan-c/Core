namespace Northwind.Infrastructure.Seeding;

/// <summary>Başlangıç verisi ayarları (appsettings: <c>Seed</c>).</summary>
public sealed class SeedOptions
{
    /// <summary>
    /// Her mağazaya <c>admin@</c>, <c>sales@</c> ve <c>warehouse@&lt;mağaza&gt;.local</c> kullanıcıları bu şifreyle
    /// oluşturulur. Boşsa kullanıcı oluşturulmaz. Yalnızca geliştirme ortamında kullan.
    /// </summary>
    public string? DemoUserPassword { get; set; }

    /// <summary>Northwind verisinin yükleneceği mağazaların kısa adları (<c>TenantInfo.Identifier</c>).</summary>
    public List<string> NorthwindTenants { get; set; } = [];
}
