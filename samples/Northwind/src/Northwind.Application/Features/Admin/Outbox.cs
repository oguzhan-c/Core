using Can.Core.Domain.Results;
using Can.Core.Application;
using Can.Core.Mediator;
using Can.Core.Persistence.Outbox;
using Can.Core.Persistence.Paging;
using FluentValidation;
using Northwind.Application.Common;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Admin;

/// <summary>
/// Panelde gösterilen outbox durumu.
/// </summary>
public enum OutboxStatus
{
    /// <summary>
    /// İlk kez yayınlanmayı bekliyor.
    /// </summary>
    Pending,

    /// <summary>
    /// Hata aldı; <c>NextAttemptAt</c> anında kendiliğinden tekrar denenecek.
    /// </summary>
    Retrying,

    /// <summary>
    /// Yayınlandı.
    /// </summary>
    Processed,

    /// <summary>
    /// Deneme hakkı tükendi ya da kalıcı hata; yeniden kuyruğa alınana kadar denenmez.
    /// </summary>
    Dead,
}

public sealed record OutboxMessageDto(
    Guid Id,
    Guid EventId,
    string EventType,
    string Payload,
    DateTime OccurredAt,
    DateTime? ProcessedAt,
    int Attempts,
    string? LastError,
    DateTime? NextAttemptAt,
    OutboxStatus Status);

/// <summary>Mağazanın outbox mesajları: kalıcı event'lerin (kargo bildirimi, ürün kaldırma ...) yayın durumu.</summary>
public sealed record GetOutboxMessagesQuery(PageRequest Page, OutboxStatus? Status = null) : IRequest<Result<IPaginate<OutboxMessageDto>>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
}

/// <summary>
/// Ölü ya da yeniden denenecek mesajı hemen yeniden kuyruğa alır (deneme sayacı sıfırlanır).
/// </summary>
public sealed record RetryOutboxMessageCommand(Guid Id) : IRequest<Result<Success>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
}

public sealed class GetOutboxMessagesQueryValidator : AbstractValidator<GetOutboxMessagesQuery>
{
    public GetOutboxMessagesQueryValidator() => RuleFor(q => q.Page).ValidPage();
}

public sealed class OutboxHandlers
    : IRequestHandler<GetOutboxMessagesQuery, Result<IPaginate<OutboxMessageDto>>>,
        IRequestHandler<RetryOutboxMessageCommand, Result<Success>>
{
    private readonly IOutboxMonitor _outbox;
    private readonly ICurrentTenant _currentTenant;

    public OutboxHandlers(IOutboxMonitor outbox, ICurrentTenant currentTenant)
    {
        _outbox = outbox;
        _currentTenant = currentTenant;
    }

    public async Task<Result<IPaginate<OutboxMessageDto>>> Handle(GetOutboxMessagesQuery request, CancellationToken cancellationToken)
    {
        string? tenantId = _currentTenant.Id?.ToString();
        IQueryable<OutboxMessage> query = _outbox.Query().Where(m => m.TenantId == tenantId);

        query = request.Status switch
        {
            OutboxStatus.Pending => query.Where(m => m.Status == OutboxMessageStatus.Pending && m.Attempts == 0),
            OutboxStatus.Retrying => query.Where(m => m.Status == OutboxMessageStatus.Pending && m.Attempts > 0),
            OutboxStatus.Processed => query.Where(m => m.Status == OutboxMessageStatus.Processed),
            OutboxStatus.Dead => query.Where(m => m.Status == OutboxMessageStatus.Dead),
            _ => query,
        };

        IPaginate<OutboxMessage> page = await query
            .OrderByDescending(m => m.OccurredAt)
            .ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken);

        return Result.Ok(page.Map(m => new OutboxMessageDto(
            m.Id,
            m.EventId,
            ShortTypeName(m.Type),
            m.Payload,
            m.OccurredAt,
            m.ProcessedAt,
            m.Attempts,
            m.LastError,
            m.NextAttemptAt,
            m.Status switch
            {
                OutboxMessageStatus.Processed => OutboxStatus.Processed,
                OutboxMessageStatus.Dead => OutboxStatus.Dead,
                _ => m.Attempts > 0 ? OutboxStatus.Retrying : OutboxStatus.Pending,
            }
        )));
    }

    public async Task<Result<Success>> Handle(RetryOutboxMessageCommand request, CancellationToken cancellationToken) =>
        await _outbox.RetryAsync(request.Id, _currentTenant.Id?.ToString(), cancellationToken)
            ? Result.Success
            : Error.NotFound("outbox.not_found", "Yeniden kuyruğa alınacak mesaj bulunamadı (zaten yayınlanmış olabilir).");

    /// <summary><c>Northwind.Domain.Orders.OrderShipped, Northwind.Domain</c> → <c>OrderShipped</c>.</summary>
    private static string ShortTypeName(string type)
    {
        string fullName = type.Split(',')[0];
        return fullName[(fullName.LastIndexOf('.') + 1)..];
    }
}
