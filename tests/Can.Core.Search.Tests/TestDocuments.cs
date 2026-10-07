using Can.Core.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.Search.Tests;

public sealed record Product(string Id, string Name, string Category, decimal Price, bool Discontinued, string? Description = null) : ISearchDocument;

internal static class SearchTestHost
{
    public static ServiceProvider Build(Action<CanSearchBuilder>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddCanMultiTenancy();
        CanSearchBuilder builder = services
            .AddCanSearch()
            .AddIndex<Product>("products", m => m.Text("name", "turkish", withKeyword: true).Text("description").Keyword("category").Double("price").Boolean("discontinued"));
        configure?.Invoke(builder);
        return services.BuildServiceProvider();
    }

    /// <summary>Verilen tenant'ın scope'unda dizin (tenant null: tenant'sız).</summary>
    public static (IServiceScope Scope, ISearchIndex<Product> Index) Index(ServiceProvider provider, string? tenant)
    {
        IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
        return (scope, scope.ServiceProvider.GetRequiredService<ISearchIndex<Product>>());
    }

    public static readonly Product[] Catalog =
    [
        new("1", "Chai Çayı", "İçecek", 18m, false, "Hint baharatlı siyah çay"),
        new("2", "Chang Birası", "İçecek", 19m, false),
        new("3", "Anason Şurubu", "Çeşni", 10m, false),
        new("4", "Bitter Çikolata", "Şekerleme", 43.9m, false, "Yüzde 70 kakao"),
        new("5", "Sütlü Çikolata", "Şekerleme", 12.5m, true),
        new("6", "Yeşil Çay", "İçecek", 25m, false, "Demlik poşet çay"),
    ];
}
