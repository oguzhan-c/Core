using Can.Core.Application;
using Can.Core.Application.Exceptions;
using Can.Core.Mediator;
using Can.Core.Persistence.Outbox;
using Can.Core.Persistence.Paging;
using FluentValidation;
using Northwind.Application.Common;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Admin;

public enum OutboxStatus
{
    Pending,
    Processed,
    Failed,
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
    OutboxStatus Status);

/// <summary>Mağazanın outbox mesajları: kalıcı event'lerin (kargo bildirimi, ürün kaldırma ...) yayın durumu.</summary>
public sealed record GetOutboxMessagesQuery(PageRequest Page, OutboxStatus? Status = null) : IRequest<IPaginate<OutboxMessageDto>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
}

/// <summary>Başarısız mesajı yeniden denemeye alır.</summary>
public sealed record RetryOutboxMessageCommand(Guid Id) : IRequest, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
}

public sealed class GetOutboxMessagesQueryValidator : AbstractValidator<GetOutboxMessagesQuery>
{
    public GetOutboxMessagesQueryValidator() => RuleFor(q => q.Page).ValidPage();
}

public sealed class OutboxHandlers
    : IRequestHandler<GetOutboxMessagesQuery, IPaginate<OutboxMessageDto>>,
        IRequestHandler<RetryOutboxMessageCommand>
{
    private readonly IOutboxMonitor _outbox;
    private readonly ICurrentTenant _currentTenant;

    public OutboxHandlers(IOutboxMonitor outbox, ICurrentTenant currentTenant)
    {
        _outbox = outbox;
        _currentTenant = currentTenant;
    }

    public async Task<IPaginate<OutboxMessageDto>> Handle(GetOutboxMessagesQuery request, CancellationToken cancellationToken)
    {
        string? tenantId = _currentTenant.Id?.ToString();
        IQueryable<OutboxMessage> query = _outbox.Query().Where(m => m.TenantId == tenantId);

        query = request.Status switch
        {
            OutboxStatus.Pending => query.Where(m => m.ProcessedAt == null && m.LastError == null),
            OutboxStatus.Processed => query.Where(m => m.ProcessedAt != null),
            OutboxStatus.Failed => query.Where(m => m.ProcessedAt == null && m.LastError != null),
            _ => query,
        };

        IPaginate<OutboxMessage> page = await query
            .OrderByDescending(m => m.OccurredAt)
            .ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken);

        return page.Map(m => new OutboxMessageDto(
            m.Id,
            m.EventId,
            ShortTypeName(m.Type),
            m.Payload,
            m.OccurredAt,
            m.ProcessedAt,
            m.Attempts,
            m.LastError,
            m.ProcessedAt is not null ? OutboxStatus.Processed
                : m.LastError is not null ? OutboxStatus.Failed
                : OutboxStatus.Pending
        ));
    }

    public async Task Handle(RetryOutboxMessageCommand request, CancellationToken cancellationToken)
    {
        if (!await _outbox.RetryAsync(request.Id, _currentTenant.Id?.ToString(), cancellationToken))
            throw new NotFoundException("Yeniden denenecek mesaj bulunamadı (zaten yayınlanmış olabilir).");
    }

    /// <summary><c>Northwind.Domain.Orders.OrderShipped, Northwind.Domain</c> → <c>OrderShipped</c>.</summary>
    private static string ShortTypeName(string type)
    {
        string fullName = type.Split(',')[0];
        return fullName[(fullName.LastIndexOf('.') + 1)..];
    }
}
