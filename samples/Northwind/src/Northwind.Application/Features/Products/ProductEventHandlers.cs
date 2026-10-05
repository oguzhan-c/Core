using Can.Core.BackgroundJobs;
using Can.Core.Mailing;
using Can.Core.Mediator;
using Can.Core.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Northwind.Application.Common;
using Northwind.Domain.Catalog;

namespace Northwind.Application.Features.Products;

/// <summary>Stok kritik seviyeye inince uyarı loglar (sipariş verilirken, aynı istek içinde).</summary>
public sealed partial class WarnWhenStockIsLow : INotificationHandler<StockBelowReorderLevel>
{
    private readonly ILogger<WarnWhenStockIsLow> _logger;

    public WarnWhenStockIsLow(ILogger<WarnWhenStockIsLow> logger) => _logger = logger;

    public Task Handle(StockBelowReorderLevel notification, CancellationToken cancellationToken)
    {
        LogLowStock(notification.ProductName, notification.UnitsInStock, notification.ReorderLevel);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "'{ProductName}' stoğu kritik seviyede: {UnitsInStock} (yeniden sipariş seviyesi {ReorderLevel}).")]
    private partial void LogLowStock(string productName, int unitsInStock, int reorderLevel);
}

/// <summary>Satıştan kaldırılan ürünü bildirir (outbox'tan, kalıcı).</summary>
public sealed partial class NotifyWhenProductDiscontinued : INotificationHandler<ProductDiscontinued>
{
    private readonly IEmailSender _emailSender;
    private readonly NotificationOptions _options;
    private readonly ILogger<NotifyWhenProductDiscontinued> _logger;

    public NotifyWhenProductDiscontinued(IEmailSender emailSender, NotificationOptions options, ILogger<NotifyWhenProductDiscontinued> logger)
    {
        _emailSender = emailSender;
        _options = options;
        _logger = logger;
    }

    public async Task Handle(ProductDiscontinued notification, CancellationToken cancellationToken)
    {
        LogDiscontinued(notification.ProductName);

        if (string.IsNullOrWhiteSpace(_options.OperationsEmail))
            return;

        var message = new EmailMessage($"'{notification.ProductName}' satıştan kaldırıldı")
        {
            TextBody = $"'{notification.ProductName}' ürünü satıştan kaldırıldı; yeni siparişlere eklenemez.",
        };
        message.To.Add(new EmailAddress(_options.OperationsEmail));

        await _emailSender.SendAsync(message, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "'{ProductName}' satıştan kaldırıldı.")]
    private partial void LogDiscontinued(string productName);
}

/// <summary>
/// Her mağaza için yeniden sipariş verilmesi gereken ürünlerin listesini e-postayla gönderen tekrarlayan iş
/// (<c>AddCanRecurringJob&lt;ReorderReportJob&gt;(o =&gt; o.PerTenant = true)</c>).
/// </summary>
public sealed class ReorderReportJob : IBackgroundJob
{
    private readonly IRepository<Product, Guid> _products;
    private readonly IEmailSender _emailSender;
    private readonly NotificationOptions _options;

    public ReorderReportJob(IRepository<Product, Guid> products, IEmailSender emailSender, NotificationOptions options)
    {
        _products = products;
        _emailSender = emailSender;
        _options = options;
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.OperationsEmail))
            return;

        var products = await _products
            .Query(enableTracking: false)
            .Where(p => !p.IsDiscontinued && p.UnitsInStock + p.UnitsOnOrder <= p.ReorderLevel)
            .OrderBy(p => p.UnitsInStock)
            .Select(p => new { p.Name, p.UnitsInStock, p.UnitsOnOrder, p.ReorderLevel })
            .ToListAsync(cancellationToken);

        if (products.Count == 0)
            return;

        var message = new EmailMessage($"Yeniden sipariş verilmesi gereken {products.Count} ürün")
        {
            TextBody = string.Join(
                Environment.NewLine,
                products.Select(p => $"- {p.Name}: stok {p.UnitsInStock}, yolda {p.UnitsOnOrder}, seviye {p.ReorderLevel}")
            ),
        };
        message.To.Add(new EmailAddress(_options.OperationsEmail));

        await _emailSender.SendAsync(message, cancellationToken);
    }
}
