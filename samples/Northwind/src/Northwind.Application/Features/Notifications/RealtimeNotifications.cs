using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Can.Core.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Northwind.Domain.Catalog;
using Northwind.Domain.Customers;
using Northwind.Domain.Identity;
using Northwind.Domain.Orders;

namespace Northwind.Application.Features.Notifications;

/// <summary>
/// Panel ve mağaza için anlık bildirimler (SignalR). Mesajlar yalnızca id ve numara taşır; istemci listeyi yeniler.
/// Bildirim gönderilemese de asıl işlem etkilenmez (hata loglanır).
/// </summary>
public static class RealtimeTypes
{
    public const string OrderPlaced = "order.placed";
    public const string OrderShipped = "order.shipped";
    public const string OrderCancelled = "order.cancelled";
    public const string StockLow = "stock.low";
}

public sealed partial class RealtimeOrderNotifications(
    IRealtimeNotifier realtime,
    IRepository<Customer, Guid> customers,
    ILogger<RealtimeOrderNotifications> logger)
    : INotificationHandler<OrderPlaced>, INotificationHandler<OrderShipped>, INotificationHandler<OrderCancelled>
{
    public Task Handle(OrderPlaced notification, CancellationToken cancellationToken) =>
        SafeAsync(async () =>
        {
            var message = new RealtimeMessage(RealtimeTypes.OrderPlaced, new { notification.OrderId, notification.OrderNumber });
            await realtime.SendToRoleAsync(Roles.Warehouse, message, cancellationToken);
            await realtime.SendToRoleAsync(Roles.Sales, message, cancellationToken);
            await realtime.SendToRoleAsync(Roles.Admin, message, cancellationToken);
        });

    /// <summary>Outbox'tan (siparişin mağazası adına) çalışır: personele ve siparişin sahibi müşteriye.</summary>
    public Task Handle(OrderShipped notification, CancellationToken cancellationToken) =>
        SafeAsync(async () =>
        {
            var message = new RealtimeMessage(RealtimeTypes.OrderShipped, new { notification.OrderId, notification.OrderNumber });
            await realtime.SendToRoleAsync(Roles.Sales, message, cancellationToken);
            await realtime.SendToRoleAsync(Roles.Admin, message, cancellationToken);

            Guid? userId = await customers
                .Query(withDeleted: true, enableTracking: false)
                .Where(c => c.Id == notification.CustomerId)
                .Select(c => c.UserId)
                .FirstOrDefaultAsync(cancellationToken);

            if (userId is { } customerUser)
                await realtime.SendToUserAsync(customerUser.ToString(), message, cancellationToken);
        });

    public Task Handle(OrderCancelled notification, CancellationToken cancellationToken) =>
        SafeAsync(async () =>
        {
            var message = new RealtimeMessage(RealtimeTypes.OrderCancelled, new { notification.OrderId, notification.OrderNumber });
            await realtime.SendToRoleAsync(Roles.Warehouse, message, cancellationToken);
            await realtime.SendToRoleAsync(Roles.Admin, message, cancellationToken);
        });

    private async Task SafeAsync(Func<Task> send)
    {
        try
        {
            await send();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Anlık bildirim gönderilemedi.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

public sealed partial class RealtimeStockNotifications(IRealtimeNotifier realtime, ILogger<RealtimeStockNotifications> logger)
    : INotificationHandler<StockBelowReorderLevel>
{
    public async Task Handle(StockBelowReorderLevel notification, CancellationToken cancellationToken)
    {
        var message = new RealtimeMessage(
            RealtimeTypes.StockLow,
            new { notification.ProductId, notification.ProductName, notification.UnitsInStock, notification.ReorderLevel }
        );

        try
        {
            await realtime.SendToRoleAsync(Roles.Warehouse, message, cancellationToken);
            await realtime.SendToRoleAsync(Roles.Admin, message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailed(logger, exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stok bildirimi gönderilemedi.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
