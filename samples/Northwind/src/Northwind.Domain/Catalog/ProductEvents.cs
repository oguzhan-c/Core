using Can.Core.Domain.Events;

namespace Northwind.Domain.Catalog;

public sealed record ProductPriceChanged(Guid ProductId, decimal OldPrice, decimal NewPrice) : DomainEvent;

/// <summary>Stok yeniden sipariş seviyesine ya da altına indi.</summary>
public sealed record StockBelowReorderLevel(Guid ProductId, string ProductName, int UnitsInStock, int ReorderLevel) : DomainEvent;

/// <summary>
/// Ürünün katalogda görünen bir bilgisi değişti (oluşturma, ad/kategori, fiyat, satıştan kaldırma, silme). Arama dizini
/// bununla güncellenir.
/// </summary>
public sealed record ProductCatalogChanged(Guid ProductId) : DomainEvent;

/// <summary>Ürün satıştan kaldırıldı. Outbox üzerinden yayınlanır (kaybolmaz).</summary>
public sealed record ProductDiscontinued(Guid ProductId, string ProductName) : DomainEvent, IIntegrationEvent;
