using Can.Core.Application;
using Can.Core.Domain.Results;
using Can.Core.Mediator;
using Can.Core.MultiTenancy;
using Can.Core.Persistence.Repositories;
using Can.Core.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Northwind.Domain.Catalog;
using AppRoles = Northwind.Domain.Identity.Roles;

namespace Northwind.Application.Features.Products;

/// <summary>
/// Arama dizinindeki ürün. Yalnızca aranan/filtrelenen alanlar: stok gibi sık değişen bilgiler dizinde tutulmaz,
/// sonuçlar veritabanından taze okunur.
/// </summary>
public sealed record ProductSearchDocument(
    string Id,
    string Name,
    string? CategoryId,
    string? CategoryName,
    string? SupplierName,
    string? QuantityPerUnit,
    decimal UnitPrice,
    bool Discontinued) : ISearchDocument;

public static class ProductSearch
{
    /// <summary>Ürün dizini: Türkçe çözümleyiciyle ad, kategori, tedarikçi ve birim aranır; fiyat ve kategori filtrelenir.</summary>
    public static CanSearchBuilder AddProductSearchIndex(this CanSearchBuilder builder) =>
        builder.AddIndex<ProductSearchDocument>("products", m => m
            .Text("name", "turkish", withKeyword: true)
            .Text("categoryName", "turkish")
            .Text("supplierName")
            .Text("quantityPerUnit")
            .Keyword("categoryId")
            .Double("unitPrice")
            .Boolean("discontinued"));

    /// <summary>Tüm mağazaların ürünlerini yeniden dizinler (açılışta; bellek içi motorda dizin her açılışta boştur).</summary>
    public static async Task<int> ReindexProductSearchAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        int total = 0;
        foreach (TenantInfo tenant in (await services.GetRequiredService<ITenantStore>().GetAllAsync(cancellationToken)).Where(t => t.IsActive))
        {
            await using AsyncServiceScope scope = services.CreateTenantScope(tenant);
            total += await scope.ServiceProvider.GetRequiredService<ProductSearchIndexer>().ReindexAsync(cancellationToken);
        }

        return total;
    }
}

/// <summary>Ürünleri aktif mağazanın arama dizinine yazar.</summary>
public sealed class ProductSearchIndexer
{
    private readonly ISearchIndex<ProductSearchDocument> _index;
    private readonly IRepository<Product, Guid> _products;
    private readonly IRepository<Category, Guid> _categories;
    private readonly IRepository<Supplier, Guid> _suppliers;

    public ProductSearchIndexer(
        ISearchIndex<ProductSearchDocument> index,
        IRepository<Product, Guid> products,
        IRepository<Category, Guid> categories,
        IRepository<Supplier, Guid> suppliers)
    {
        _index = index;
        _products = products;
        _categories = categories;
        _suppliers = suppliers;
    }

    /// <summary>Mağazanın dizinini baştan kurar; dizine yazılan ürün sayısı.</summary>
    public async Task<int> ReindexAsync(CancellationToken cancellationToken)
    {
        List<ProductSearchDocument> documents = await LoadAsync(_products.Query(enableTracking: false), cancellationToken);
        await _index.DeleteAllAsync(cancellationToken);
        SearchBulkResult result = await _index.IndexManyAsync(documents, cancellationToken);
        if (result.HasFailures)
            throw new SearchException($"{result.Failures.Count} ürün dizine yazılamadı: {result.Failures[0].Reason}");
        return result.Succeeded;
    }

    /// <summary>Tek ürünü günceller; silinmişse dizinden çıkarır.</summary>
    public async Task SyncAsync(Guid productId, CancellationToken cancellationToken)
    {
        List<ProductSearchDocument> documents = await LoadAsync(_products.Query(enableTracking: false).Where(p => p.Id == productId), cancellationToken);
        if (documents.Count == 0)
            await _index.DeleteAsync(productId.ToString(), cancellationToken);
        else
            await _index.IndexAsync(documents[0], cancellationToken);
    }

    private async Task<List<ProductSearchDocument>> LoadAsync(IQueryable<Product> products, CancellationToken cancellationToken)
    {
        IQueryable<Category> categories = _categories.Query(withDeleted: true, enableTracking: false);
        IQueryable<Supplier> suppliers = _suppliers.Query(withDeleted: true, enableTracking: false);

        var rows = await products
            .Select(p => new
            {
                p.Id,
                p.Name,
                p.CategoryId,
                CategoryName = categories.Where(c => c.Id == p.CategoryId).Select(c => c.Name).FirstOrDefault(),
                SupplierName = suppliers.Where(s => s.Id == p.SupplierId).Select(s => s.CompanyName).FirstOrDefault(),
                p.QuantityPerUnit,
                p.UnitPrice,
                p.IsDiscontinued,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new ProductSearchDocument(
                r.Id.ToString(),
                r.Name,
                r.CategoryId?.ToString(),
                r.CategoryName,
                r.SupplierName,
                r.QuantityPerUnit,
                r.UnitPrice,
                r.IsDiscontinued))
            .ToList();
    }
}

/// <summary>
/// Katalog değişince ürünü arama dizininde günceller (kayıttan sonra, aynı istekte). Arama motoruna ulaşılamazsa istek
/// bozulmaz: uyarı loglanır, dizin yönetim panelinden ya da sonraki açılışta yeniden kurulur.
/// </summary>
public sealed partial class SyncProductSearchIndex : INotificationHandler<ProductCatalogChanged>
{
    private readonly ProductSearchIndexer _indexer;
    private readonly TenantContext _tenantContext;
    private readonly ILogger<SyncProductSearchIndex> _logger;

    public SyncProductSearchIndex(ProductSearchIndexer indexer, TenantContext tenantContext, ILogger<SyncProductSearchIndex> logger)
    {
        _indexer = indexer;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    public async Task Handle(ProductCatalogChanged notification, CancellationToken cancellationToken)
    {
        // Tenant'sız çalışan işler (ör. ilk kurulum) ürünü yanlış kapsama yazmasın; açılıştaki yeniden dizinleme kapsar.
        if (_tenantContext.TenantId is null)
            return;

        try
        {
            await _indexer.SyncAsync(notification.ProductId, cancellationToken);
        }
        catch (SearchException ex)
        {
            LogFailed(_logger, ex, notification.ProductId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ürün {ProductId} arama dizinine yazılamadı; dizin yeniden kurulana kadar aramada eski hâli görünebilir.")]
    private static partial void LogFailed(ILogger logger, Exception exception, Guid productId);
}

// ---------------------------------------------------------------- yönetim

/// <summary>Mağazanın ürün arama dizinini veritabanından yeniden kurar.</summary>
public sealed record ReindexProductSearchCommand : IRequest<Result<int>>, ISecuredRequest, ILoggableRequest
{
    public IReadOnlyCollection<string> Roles => [AppRoles.Admin];
}

public sealed class ReindexProductSearchCommandHandler : IRequestHandler<ReindexProductSearchCommand, Result<int>>
{
    private readonly ProductSearchIndexer _indexer;

    public ReindexProductSearchCommandHandler(ProductSearchIndexer indexer) => _indexer = indexer;

    public async Task<Result<int>> Handle(ReindexProductSearchCommand request, CancellationToken cancellationToken) =>
        await _indexer.ReindexAsync(cancellationToken);
}
