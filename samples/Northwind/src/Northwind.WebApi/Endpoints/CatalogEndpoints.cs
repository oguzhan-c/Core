using Can.Core.Mediator;
using Can.Core.WebApi;
using Can.Core.Persistence.Dynamic;
using Microsoft.AspNetCore.Mvc;
using Northwind.Application.Features.Categories;
using Northwind.Application.Features.Employees;
using Northwind.Application.Features.Products;
using Northwind.Application.Features.Shippers;
using Northwind.Application.Features.Suppliers;

namespace Northwind.WebApi.Endpoints;

internal static class CatalogEndpoints
{
    public sealed record CategoryRequest(string Name, string? Description);

    public sealed record ProductRequest(string Name, Guid? CategoryId, Guid? SupplierId, string? QuantityPerUnit, int ReorderLevel);

    public sealed record CreateProductRequest(
        string Name,
        Guid? CategoryId,
        Guid? SupplierId,
        string? QuantityPerUnit,
        decimal UnitPrice,
        int UnitsInStock,
        int ReorderLevel);

    public sealed record PriceRequest(decimal UnitPrice);

    public sealed record RestockRequest(int Quantity);

    public sealed record ProductListParameters(int Index = 0, int Size = 20, Guid? CategoryId = null, string? Search = null, bool IncludeDiscontinued = false);

    public static void MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        // ------------------------------------------------------------ kategoriler

        RouteGroupBuilder categories = app.MapGroup("/categories").WithTags("Categories").RequireAuthorization();

        categories.MapGet("/", (ISender sender, CancellationToken ct) => sender.Send(new GetCategoryListQuery(), ct).ToHttpResult());

        categories.MapGet("/{id:guid}", (Guid id, ISender sender, CancellationToken ct) => sender.Send(new GetCategoryByIdQuery(id), ct).ToHttpResult());

        categories.MapPost("/", async (CategoryRequest body, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(new CreateCategoryCommand(body.Name, body.Description), ct).ToHttpResult(id => TypedResults.Created($"/api/categories/{id}", new { id }));
        });

        categories.MapPut("/{id:guid}", async (Guid id, CategoryRequest body, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(new UpdateCategoryCommand(id, body.Name, body.Description), ct).ToHttpResult();
        });

        categories.MapDelete("/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(new DeleteCategoryCommand(id), ct).ToHttpResult();
        });

        // ------------------------------------------------------------ ürünler

        RouteGroupBuilder products = app.MapGroup("/products").WithTags("Products").RequireAuthorization();

        products.MapGet("/", ([AsParameters] ProductListParameters p, ISender sender, CancellationToken ct) =>
            sender.Send(new GetProductListQuery(new(p.Index, p.Size), p.CategoryId, p.Search, p.IncludeDiscontinued), ct).ToHttpResult());

        products.MapPost("/search", ([FromBody] DynamicQuery query, [AsParameters] PageParameters page, ISender sender, CancellationToken ct) =>
                sender.Send(new SearchProductsQuery(query, page.ToRequest()), ct).ToHttpResult())
            .WithSummary("Dinamik filtre ve sıralama ile ürün arama (örnekler: GET /api/search/examples).");

        products.MapGet("/to-reorder", (ISender sender, CancellationToken ct) => sender.Send(new GetProductsToReorderQuery(), ct).ToHttpResult())
            .WithSummary("Yeniden sipariş verilmesi gereken ürünler.");

        products.MapGet("/{id:guid}", (Guid id, ISender sender, CancellationToken ct) => sender.Send(new GetProductByIdQuery(id), ct).ToHttpResult());

        products.MapPost("/", async (CreateProductRequest body, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(
                new CreateProductCommand(body.Name, body.CategoryId, body.SupplierId, body.QuantityPerUnit, body.UnitPrice, body.UnitsInStock, body.ReorderLevel),
                ct
            ).ToHttpResult(id => TypedResults.Created($"/api/products/{id}", new { id }));
        });

        products.MapPut("/{id:guid}", async (Guid id, ProductRequest body, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(new UpdateProductCommand(id, body.Name, body.CategoryId, body.SupplierId, body.QuantityPerUnit, body.ReorderLevel), ct).ToHttpResult();
        });

        products.MapPut("/{id:guid}/price", async (Guid id, PriceRequest body, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(new ChangeProductPriceCommand(id, body.UnitPrice), ct).ToHttpResult();
        });

        products.MapPost("/{id:guid}/restock", async (Guid id, RestockRequest body, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(new RestockProductCommand(id, body.Quantity), ct).ToHttpResult();
        });

        products.MapPost("/{id:guid}/discontinue", async (Guid id, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(new DiscontinueProductCommand(id), ct).ToHttpResult();
        });

        products.MapDelete("/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(new DeleteProductCommand(id), ct).ToHttpResult();
        });

        // ------------------------------------------------------------ salt-okunur listeler

        app.MapGet("/suppliers", ([AsParameters] PageParameters page, string? country, ISender sender, CancellationToken ct) =>
                sender.Send(new GetSupplierListQuery(page.ToRequest(), country), ct).ToHttpResult())
            .WithTags("Suppliers")
            .RequireAuthorization();

        app.MapGet("/shippers", (ISender sender, CancellationToken ct) => sender.Send(new GetShipperListQuery(), ct).ToHttpResult())
            .WithTags("Shippers")
            .RequireAuthorization();

        app.MapGet("/employees", (ISender sender, CancellationToken ct) => sender.Send(new GetEmployeeListQuery(), ct).ToHttpResult())
            .WithTags("Employees")
            .RequireAuthorization();
    }
}
