using Can.Core.Mediator;
using Can.Core.WebApi;
using Can.Core.Persistence.Dynamic;
using Microsoft.AspNetCore.Mvc;
using Northwind.Application.Common;
using Northwind.Application.Features.AuditLogs;
using Northwind.Application.Features.Customers;
using Northwind.Application.Features.Orders;
using Northwind.Application.Features.Reports;
using Northwind.Domain.Orders;

namespace Northwind.WebApi.Endpoints;

internal static class SalesEndpoints
{
    public sealed record CustomerRequest(
        string CompanyName,
        string? ContactName,
        string? ContactTitle,
        AddressDto? Address,
        string? Phone,
        string? Fax);

    public sealed record CreateCustomerRequest(
        string Code,
        string CompanyName,
        string? ContactName,
        string? ContactTitle,
        AddressDto? Address,
        string? Phone,
        string? Fax);

    public sealed record CustomerListParameters(int Index = 0, int Size = 20, string? Search = null, string? Country = null);

    public sealed record OrderListParameters(
        int Index = 0,
        int Size = 20,
        Guid? CustomerId = null,
        OrderStatus? Status = null,
        DateOnly? From = null,
        DateOnly? To = null);

    public sealed record ShipRequest(Guid ShipperId);

    public sealed record PlaceOrderRequest(
        Guid CustomerId,
        Guid? EmployeeId,
        DateOnly? RequiredDate,
        decimal Freight,
        ShipToDto? ShipTo,
        IReadOnlyList<PlaceOrderLine> Lines);

    public sealed record ReportParameters(DateOnly? From = null, DateOnly? To = null);

    public sealed record AuditLogParameters(int Index = 0, int Size = 20, string? EntityType = null, string? EntityId = null);

    public static void MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder customers = app.MapGroup("/customers").WithTags("Customers").RequireAuthorization();

        customers.MapGet("/", ([AsParameters] CustomerListParameters p, ISender sender, CancellationToken ct) =>
            sender.Send(new GetCustomerListQuery(new(p.Index, p.Size), p.Search, p.Country), ct).ToHttpResult());

        customers.MapPost("/search", ([FromBody] DynamicQuery query, [AsParameters] PageParameters page, ISender sender, CancellationToken ct) =>
                sender.Send(new SearchCustomersQuery(query, page.ToRequest()), ct).ToHttpResult())
            .WithSummary("Dinamik filtre ve sıralama ile müşteri arama (örnekler: GET /api/search/examples).");

        customers.MapGet("/{id:guid}", (Guid id, ISender sender, CancellationToken ct) => sender.Send(new GetCustomerByIdQuery(id), ct).ToHttpResult());

        customers.MapGet("/{id:guid}/orders", ([AsParameters] PageParameters page, Guid id, ISender sender, CancellationToken ct) =>
            sender.Send(new GetOrderListQuery(page.ToRequest(), CustomerId: id), ct).ToHttpResult());

        customers.MapPost("/", async (CreateCustomerRequest body, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(
                new CreateCustomerCommand(body.Code, body.CompanyName, body.ContactName, body.ContactTitle, body.Address, body.Phone, body.Fax),
                ct
            ).ToHttpResult(id => TypedResults.Created($"/api/customers/{id}", new { id }));
        });

        customers.MapPut("/{id:guid}", async (Guid id, CustomerRequest body, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(new UpdateCustomerCommand(id, body.CompanyName, body.ContactName, body.ContactTitle, body.Address, body.Phone, body.Fax), ct).ToHttpResult();
        });

        customers.MapDelete("/{id:guid}", async (Guid id, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(new DeleteCustomerCommand(id), ct).ToHttpResult();
        });
    }

    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder orders = app.MapGroup("/orders").WithTags("Orders").RequireAuthorization();

        orders.MapGet("/", ([AsParameters] OrderListParameters p, ISender sender, CancellationToken ct) =>
            sender.Send(new GetOrderListQuery(new(p.Index, p.Size), p.CustomerId, p.Status, p.From, p.To), ct).ToHttpResult());

        orders.MapPost("/search", ([FromBody] DynamicQuery query, [AsParameters] PageParameters page, ISender sender, CancellationToken ct) =>
                sender.Send(new SearchOrdersQuery(query, page.ToRequest()), ct).ToHttpResult())
            .WithSummary("Dinamik filtre ve sıralama ile sipariş arama (örnekler: GET /api/search/examples).");

        orders.MapGet("/{id:guid}", (Guid id, ISender sender, CancellationToken ct) => sender.Send(new GetOrderByIdQuery(id), ct).ToHttpResult());

        orders.MapPost("/", async (PlaceOrderRequest body, ISender sender, CancellationToken ct) =>
            {
                return await sender.Send(
                    new PlaceOrderCommand(body.CustomerId, body.EmployeeId, body.RequiredDate, body.Freight, body.ShipTo, body.Lines ?? []),
                    ct
                ).ToHttpResult(result => TypedResults.Created($"/api/orders/{result.Id}", result));
            })
            .WithSummary("Yeni sipariş: stok düşülür, numara verilir (tek transaction).");

        orders.MapPost("/{id:guid}/ship", async (Guid id, ShipRequest body, ISender sender, CancellationToken ct) =>
            {
                return await sender.Send(new ShipOrderCommand(id, body.ShipperId), ct).ToHttpResult();
            })
            .WithSummary("Kargoya verir; bildirim outbox üzerinden gönderilir.");

        orders.MapPost("/{id:guid}/cancel", async (Guid id, ISender sender, CancellationToken ct) =>
            {
                return await sender.Send(new CancelOrderCommand(id), ct).ToHttpResult();
            })
            .WithSummary("İptal eder; ürünler stoğa geri konur.");
    }

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder reports = app.MapGroup("/reports").WithTags("Reports").RequireAuthorization();

        reports.MapGet("/sales-by-category", ([AsParameters] ReportParameters p, ISender sender, CancellationToken ct) =>
            sender.Send(new GetSalesByCategoryQuery(p.From, p.To), ct).ToHttpResult());

        reports.MapGet("/top-customers", ([AsParameters] ReportParameters p, int? count, ISender sender, CancellationToken ct) =>
            sender.Send(new GetTopCustomersQuery(count ?? 10, p.From, p.To), ct).ToHttpResult());

        app.MapGet("/audit-logs", ([AsParameters] AuditLogParameters p, ISender sender, CancellationToken ct) =>
                sender.Send(new GetAuditLogsQuery(new(p.Index, p.Size), p.EntityType, p.EntityId), ct).ToHttpResult())
            .WithTags("Audit")
            .RequireAuthorization()
            .WithSummary("Değişiklik geçmişi (yalnızca yönetici). Ör. ?entityType=Product&entityId=...");
    }
}
