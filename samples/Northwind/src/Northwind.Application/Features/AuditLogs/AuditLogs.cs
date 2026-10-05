using Can.Core.Application;
using Can.Core.Mediator;
using Can.Core.Persistence.AuditTrail;
using Can.Core.Persistence.Paging;
using FluentValidation;
using Northwind.Application.Common;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.AuditLogs;

public sealed record AuditLogDto(Guid Id, string EntityType, string EntityId, string Action, string? Changes, string? UserId, DateTimeOffset Timestamp, string? TraceId);

/// <summary>Mağazanın değişiklik geçmişi (yalnızca yöneticiler). Ör. bir ürünün fiyat geçmişi: EntityType=Product, EntityId=...</summary>
public sealed record GetAuditLogsQuery(PageRequest Page, string? EntityType = null, string? EntityId = null)
    : IRequest<IPaginate<AuditLogDto>>, ISecuredRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
}

public sealed class GetAuditLogsQueryValidator : AbstractValidator<GetAuditLogsQuery>
{
    public GetAuditLogsQueryValidator()
    {
        RuleFor(q => q.Page).ValidPage();
        RuleFor(q => q.EntityType).MaximumLength(256);
        RuleFor(q => q.EntityId).MaximumLength(128);
    }
}

public sealed class GetAuditLogsQueryHandler : IRequestHandler<GetAuditLogsQuery, IPaginate<AuditLogDto>>
{
    private readonly IAuditLogReader _auditLogs;
    private readonly ICurrentTenant _currentTenant;

    public GetAuditLogsQueryHandler(IAuditLogReader auditLogs, ICurrentTenant currentTenant)
    {
        _auditLogs = auditLogs;
        _currentTenant = currentTenant;
    }

    public async Task<IPaginate<AuditLogDto>> Handle(GetAuditLogsQuery request, CancellationToken cancellationToken)
    {
        // Audit log'lara tenant filtresi otomatik uygulanmaz: yalnızca aktif mağazanın kayıtları.
        string? tenantId = _currentTenant.Id?.ToString();
        IQueryable<AuditLog> query = _auditLogs.Query().Where(l => l.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(request.EntityType))
            query = query.Where(l => l.EntityType == request.EntityType);

        if (!string.IsNullOrWhiteSpace(request.EntityId))
            query = query.Where(l => l.EntityId == request.EntityId);

        IPaginate<AuditLog> page = await query
            .OrderByDescending(l => l.Timestamp)
            .ToPaginateAsync(request.Page.Index, request.Page.Size, cancellationToken: cancellationToken);

        return page.Map(l => new AuditLogDto(l.Id, l.EntityType, l.EntityId, l.Action.ToString(), l.Changes, l.UserId, l.Timestamp, l.TraceId));
    }
}
