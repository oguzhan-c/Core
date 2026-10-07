using System.Text.Json;

namespace Can.Core.Reporting;

/// <summary>
/// Kaydedilmiş rapor tanımı. Sahibine aittir; <see cref="IsShared"/> ise aynı tenant'taki herkes görür ve çalıştırır
/// (değiştirme/silme sahibe ya da yöneticiye kalır). Tanım JSON olarak saklanır: motor geliştikçe şema değişmez.
/// </summary>
public sealed class SavedReport
{
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Veri kaynağının adı (listelerde tanımı açmadan yetki denetimi için).</summary>
    public string DataSource { get; set; } = string.Empty;

    /// <summary><see cref="ReportDefinition"/>'ın JSON'u.</summary>
    public string Definition { get; set; } = "{}";

    /// <summary>Tenant (çok kiracılı olmayan uygulamalarda boş).</summary>
    public string? TenantId { get; set; }

    public string OwnerId { get; set; } = string.Empty;

    public string? OwnerName { get; set; }

    public bool IsShared { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ReportDefinition ReadDefinition() => JsonSerializer.Deserialize<ReportDefinition>(Definition, Json) ?? new ReportDefinition();

    public void WriteDefinition(ReportDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        DataSource = definition.DataSource ?? string.Empty;
        Definition = JsonSerializer.Serialize(definition, Json);
    }
}

/// <summary>Kaydedilmiş raporların deposu. Erişim kuralları (sahip/paylaşım/yetki) uç nokta katmanındadır.</summary>
public interface ISavedReportStore
{
    /// <summary>Tenant'taki, kullanıcının kendi raporları ve paylaşılanlar (ada göre).</summary>
    Task<IReadOnlyList<SavedReport>> ListAsync(string? tenantId, string userId, CancellationToken cancellationToken = default);

    Task<SavedReport?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    Task AddAsync(SavedReport report, CancellationToken cancellationToken = default);

    Task UpdateAsync(SavedReport report, CancellationToken cancellationToken = default);

    Task DeleteAsync(SavedReport report, CancellationToken cancellationToken = default);
}

/// <summary>Bellek içi depo (testler ve deneme; uygulama yeniden başlayınca kaybolur). EF Core deposu için <c>Can.Core.Reporting.EntityFrameworkCore</c>.</summary>
public sealed class InMemorySavedReportStore : ISavedReportStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, SavedReport> _reports = new();

    public Task<IReadOnlyList<SavedReport>> ListAsync(string? tenantId, string userId, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<SavedReport> list = _reports.Values
            .Where(r => r.TenantId == tenantId && (r.OwnerId == userId || r.IsShared))
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return Task.FromResult(list);
    }

    public Task<SavedReport?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_reports.GetValueOrDefault(id));

    public Task AddAsync(SavedReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!_reports.TryAdd(report.Id, report))
            throw new InvalidOperationException($"'{report.Id}' raporu zaten var.");
        return Task.CompletedTask;
    }

    public Task UpdateAsync(SavedReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        _reports[report.Id] = report;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(SavedReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        _reports.TryRemove(report.Id, out _);
        return Task.CompletedTask;
    }
}
