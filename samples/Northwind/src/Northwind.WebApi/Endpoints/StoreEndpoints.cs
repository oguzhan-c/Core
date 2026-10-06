using Can.Core.Mediator;
using Can.Core.WebApi;
using Northwind.Application.Features.Orders;
using Northwind.Application.Features.Store;

namespace Northwind.WebApi.Endpoints;

/// <summary>Mağaza sitesi: herkese açık katalog ve giriş yapmış müşterinin sepet/sipariş işlemleri.</summary>
internal static class StoreEndpoints
{
    public sealed record StoreProductParameters(
        int Index = 0,
        int Size = 12,
        Guid? CategoryId = null,
        string? Search = null,
        StoreProductSort Sort = StoreProductSort.Name);

    public sealed record CheckoutRequest(IReadOnlyList<CheckoutLine> Lines, ShipToDto? ShipTo);

    public static void MapStoreEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder store = app.MapGroup("/store").WithTags("Store");

        // ------------------------------------------------------------ katalog (anonim)

        store.MapGet("/tenants", (ISender sender, CancellationToken ct) => sender.Send(new GetStoreTenantsQuery(), ct).ToHttpResult())
            .AllowAnonymous()
            .WithSummary("Mağazalar.");

        store.MapGet("/{tenant}/categories", (string tenant, ISender sender, CancellationToken ct) =>
                sender.Send(new GetStoreCategoriesQuery(tenant), ct).ToHttpResult())
            .AllowAnonymous();

        store.MapGet("/{tenant}/products", (string tenant, [AsParameters] StoreProductParameters p, ISender sender, CancellationToken ct) =>
                sender.Send(new GetStoreProductsQuery(tenant, new(p.Index, p.Size), p.CategoryId, p.Search, p.Sort), ct).ToHttpResult())
            .AllowAnonymous();

        store.MapGet("/{tenant}/products/{id:guid}", (string tenant, Guid id, ISender sender, CancellationToken ct) =>
                sender.Send(new GetStoreProductQuery(tenant, id), ct).ToHttpResult())
            .AllowAnonymous();

        // ------------------------------------------------------------ müşteri (giriş gerekli; mağaza token'dan)

        RouteGroupBuilder my = store.MapGroup("/my").RequireAuthorization();

        my.MapPost("/checkout", async (CheckoutRequest body, ISender sender, CancellationToken ct) =>
            {
                return await sender.Send(new CheckoutCommand(body.Lines ?? [], body.ShipTo), ct).ToHttpResult(result => TypedResults.Created($"/api/store/my/orders/{result.Id}", result));
            })
            .WithSummary("Sepeti siparişe çevirir.");

        my.MapGet("/orders", ([AsParameters] PageParameters page, ISender sender, CancellationToken ct) =>
            sender.Send(new GetMyOrdersQuery(page.ToRequest()), ct).ToHttpResult());

        my.MapGet("/orders/{id:guid}", (Guid id, ISender sender, CancellationToken ct) => sender.Send(new GetMyOrderQuery(id), ct).ToHttpResult());

        my.MapPost("/orders/{id:guid}/cancel", async (Guid id, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(new CancelMyOrderCommand(id), ct).ToHttpResult();
        });
    }
}
