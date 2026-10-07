using Can.Core.Application.DependencyInjection;
using Can.Core.BackgroundJobs;
using Can.Core.Mapping.DependencyInjection;
using Can.Core.Realtime;
using Can.Core.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Northwind.Application.Common;
using Northwind.Application.Features.Auth;
using Northwind.Application.Features.Products;
using Northwind.Domain.Identity;

namespace Northwind.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Handler'lar, pipeline davranışları (yetki, doğrulama, önbellek, transaction), validator'lar, business rule'lar,
    /// mapping profilleri ve arka plan işleri.
    /// </summary>
    public static IServiceCollection AddNorthwindApplication(this IServiceCollection services, Action<NotificationOptions>? notifications = null)
    {
        var assembly = typeof(DependencyInjection).Assembly;

        services.AddCanApplication(o => o.AdminRole = Roles.Admin, assembly);
        services.AddCanMapping(assembly);
        services.AddCanBackgroundJobs(assembly);
        services.AddCanRealtimeDefaults(); // WebApi SignalR'ı kurar; diğer ortamlarda bildirim gönderilmez

        var options = new NotificationOptions();
        notifications?.Invoke(options);
        services.TryAddSingleton(options);

        services.AddScoped<AuthTokenIssuer>();
        services.AddScoped<EmailVerification>();
        services.AddScoped<TwoFactorCodeVerifier>();
        services.AddScoped<StoreTenant>();
        services.AddScoped<Features.Orders.OrderPlacer>();
        services.AddScoped<Features.Orders.OrderDetails>();
        services.AddScoped<Features.Store.CurrentCustomer>();

        // Ürün araması: motor verilmezse bellek içi (WebApi Elasticsearch'ü seçebilir: Search:Elasticsearch:Url).
        services.AddCanSearch().AddProductSearchIndex();
        services.AddScoped<Features.Products.ProductSearchIndexer>();
        return services;
    }
}
