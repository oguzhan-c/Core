using Can.Core.Mailing;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Northwind.Application.Common;
using Northwind.Domain.Catalog;
using Northwind.Domain.Customers;
using Northwind.Domain.Orders;

namespace Northwind.Application.Features.Orders;

/// <summary>
/// İptal edilen siparişin ürünlerini stoğa geri koyar. Kayıttan hemen sonra, aynı transaction içinde çalışır
/// (<c>CancelOrderCommand</c> transactional); burada hata olursa iptal de geri alınır.
/// </summary>
public sealed class ReleaseStockWhenOrderCancelled : INotificationHandler<OrderCancelled>
{
    private readonly IRepository<Product, Guid> _products;
    private readonly IUnitOfWork _unitOfWork;

    public ReleaseStockWhenOrderCancelled(IRepository<Product, Guid> products, IUnitOfWork unitOfWork)
    {
        _products = products;
        _unitOfWork = unitOfWork;
    }

    public async Task Handle(OrderCancelled notification, CancellationToken cancellationToken)
    {
        Guid[] productIds = notification.Lines.Select(l => l.ProductId).Distinct().ToArray();
        Dictionary<Guid, Product> products = await _products
            .Query(withDeleted: true)
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        foreach (OrderedQuantity line in notification.Lines)
        {
            if (products.TryGetValue(line.ProductId, out Product? product))
                product.ReleaseStock(line.Quantity);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Kargoya verilen siparişi operasyon ekibine bildirir. <see cref="OrderShipped"/> bir integration event olduğu için
/// outbox'tan, siparişin mağazası adına çalışır; e-posta gönderilemezse daha sonra tekrar denenir.
/// </summary>
public sealed partial class NotifyWhenOrderShipped : INotificationHandler<OrderShipped>
{
    private readonly IRepository<Customer, Guid> _customers;
    private readonly IEmailSender _emailSender;
    private readonly NotificationOptions _options;
    private readonly ILogger<NotifyWhenOrderShipped> _logger;

    public NotifyWhenOrderShipped(
        IRepository<Customer, Guid> customers,
        IEmailSender emailSender,
        NotificationOptions options,
        ILogger<NotifyWhenOrderShipped> logger)
    {
        _customers = customers;
        _emailSender = emailSender;
        _options = options;
        _logger = logger;
    }

    public async Task Handle(OrderShipped notification, CancellationToken cancellationToken)
    {
        LogShipped(notification.OrderNumber, notification.Total);

        if (string.IsNullOrWhiteSpace(_options.OperationsEmail))
            return;

        string customer = await _customers
            .Query(withDeleted: true, enableTracking: false)
            .Where(c => c.Id == notification.CustomerId)
            .Select(c => c.CompanyName)
            .FirstOrDefaultAsync(cancellationToken) ?? "?";

        var message = new EmailMessage($"Sipariş #{notification.OrderNumber} kargoya verildi")
        {
            TextBody = $"{customer} müşterisinin #{notification.OrderNumber} numaralı siparişi kargoya verildi. Tutar: {notification.Total:N2}.",
        };
        message.To.Add(new EmailAddress(_options.OperationsEmail));

        await _emailSender.SendAsync(message, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sipariş #{OrderNumber} kargoya verildi (tutar {Total}).")]
    private partial void LogShipped(int orderNumber, decimal total);
}
