using Can.Core.Domain.Events;

namespace Northwind.Domain.Orders;

public sealed record OrderPlaced(Guid OrderId, int OrderNumber, Guid CustomerId) : DomainEvent;

/// <summary>Sipariş kargoya verildi. Outbox üzerinden yayınlanır; bildirim e-postası bununla gönderilir.</summary>
public sealed record OrderShipped(Guid OrderId, int OrderNumber, Guid CustomerId, Guid ShipperId, decimal Total) : DomainEvent, IIntegrationEvent;

/// <summary>Sipariş iptal edildi; satırlardaki ürünler stoğa geri konur.</summary>
public sealed record OrderCancelled(Guid OrderId, int OrderNumber, IReadOnlyList<OrderedQuantity> Lines) : DomainEvent;

public sealed record OrderedQuantity(Guid ProductId, int Quantity);
