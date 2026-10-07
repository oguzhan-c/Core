using Can.Core.Mediator;
using Can.Core.WebApi;
using MimeKit;
using Northwind.Application.Features.Admin;
using Northwind.Application.Features.Products;

namespace Northwind.WebApi.Endpoints;

/// <summary>Yönetim paneline özel uçlar: özet, outbox izleme, kullanıcılar, arka plan işleri.</summary>
internal static class AdminEndpoints
{
    public sealed record OutboxParameters(int Index = 0, int Size = 20, OutboxStatus? Status = null);

    public sealed record UserParameters(int Index = 0, int Size = 20, string? Search = null, string? Role = null);

    public sealed record MailDto(string Id, DateTimeOffset Date, string? From, string To, string Subject, string? Text, string? Html);

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        RouteGroupBuilder admin = app.MapGroup("/admin").WithTags("Admin").RequireAuthorization();

        admin.MapGet("/dashboard", (ISender sender, CancellationToken ct) => sender.Send(new GetDashboardQuery(), ct).ToHttpResult());

        admin.MapGet("/outbox", ([AsParameters] OutboxParameters p, ISender sender, CancellationToken ct) =>
                sender.Send(new GetOutboxMessagesQuery(new(p.Index, p.Size), p.Status), ct).ToHttpResult())
            .WithSummary("Kalıcı event'lerin (outbox) yayın durumu.");

        admin.MapPost("/outbox/{id:guid}/retry", async (Guid id, ISender sender, CancellationToken ct) =>
        {
            return await sender.Send(new RetryOutboxMessageCommand(id), ct).ToHttpResult();
        });

        admin.MapGet("/users", ([AsParameters] UserParameters p, ISender sender, CancellationToken ct) =>
            sender.Send(new GetUsersQuery(new(p.Index, p.Size), p.Search, p.Role), ct).ToHttpResult());

        admin.MapPost("/jobs/reorder-report", async (ISender sender, CancellationToken ct) =>
            {
                return await sender.Send(new RunReorderReportCommand(), ct).ToHttpResult(_ => TypedResults.Accepted((string?)null));
            })
            .WithSummary("Yeniden sipariş raporunu arka planda hemen çalıştırır.");

        admin.MapPost("/search/products/reindex", async (ISender sender, CancellationToken ct) =>
                (await sender.Send(new ReindexProductSearchCommand(), ct)).ToHttpResult(count => TypedResults.Ok(new { indexed = count })))
            .WithSummary("Mağazanın ürün arama dizinini veritabanından yeniden kurar.");
    }

    /// <summary>
    /// YALNIZCA geliştirme: e-posta klasörüne yazılan mailleri listeler (kayıt doğrulama kodları, sipariş bildirimleri).
    /// </summary>
    public static void MapDevMailbox(this IEndpointRouteBuilder app, string directory)
    {
        app.MapGet("/api/dev/mailbox", () =>
            {
                if (!Directory.Exists(directory))
                    return new List<MailDto>();

                return new DirectoryInfo(directory)
                    .GetFiles("*.eml")
                    .OrderByDescending(f => f.CreationTimeUtc)
                    .Take(50)
                    .Select(file =>
                    {
                        using MimeMessage message = MimeMessage.Load(file.FullName);
                        return new MailDto(
                            file.Name,
                            message.Date,
                            message.From.ToString(),
                            message.To.ToString(),
                            message.Subject ?? "",
                            message.TextBody,
                            message.HtmlBody
                        );
                    })
                    .ToList();
            })
            .WithTags("Development")
            .AllowAnonymous()
            .WithSummary("Geliştirme posta kutusu (yalnızca Development).");
    }
}
