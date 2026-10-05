namespace Northwind.WebApi.Endpoints;

internal static class EndpointRegistration
{
    /// <summary>Tüm API'yi <c>/api</c> altında toplar; geri kalan adresler React uygulamasına bırakılır.</summary>
    public static IEndpointRouteBuilder MapNorthwindEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder api = app.MapGroup("/api");

        api.MapGet("/health", () => TypedResults.Ok(new { status = "ok" })).ExcludeFromDescription();
        api.MapAuthEndpoints();
        api.MapStoreEndpoints();
        api.MapCatalogEndpoints();
        api.MapCustomerEndpoints();
        api.MapOrderEndpoints();
        api.MapReportEndpoints();
        api.MapAdminEndpoints();
        api.MapSearchExampleEndpoints();

        // Bilinmeyen API adresleri index.html değil 404 dönsün.
        api.MapFallback(() => TypedResults.NotFound()).ExcludeFromDescription();
        return app;
    }
}

/// <summary>Liste endpoint'lerinde ortak sayfa parametreleri (<c>?index=0&amp;size=20</c>).</summary>
internal sealed record PageParameters(int Index = 0, int Size = 20)
{
    public Northwind.Application.Common.PageRequest ToRequest() => new(Index, Size);
}
