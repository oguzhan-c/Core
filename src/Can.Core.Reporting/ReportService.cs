using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Reporting;

/// <summary>Raporlama ayarları (motor + etiketler). <see cref="Culture"/> boşsa isteğin kültürü kullanılır.</summary>
public sealed class CanReportingOptions
{
    public CultureInfo? Culture { get; set; }

    public int MaxCells { get; set; } = 2_000_000;

    public string NullLabel { get; set; } = "(Boş)";

    public string OthersLabel { get; set; } = "Diğer";

    public string GrandTotalLabel { get; set; } = "Genel toplam";

    public string TrueLabel { get; set; } = "Evet";

    public string FalseLabel { get; set; } = "Hayır";
}

/// <summary>Raporları çalıştırır: tanımdaki veri kaynağını bulur, veritabanında ya da bellekte hesaplar.</summary>
public interface IReportService
{
    IReadOnlyList<IReportDataSource> DataSources { get; }

    IReportDataSource? Find(string name);

    /// <exception cref="ReportDefinitionException">Veri kaynağı yok ya da tanım geçersiz.</exception>
    Task<ReportResult> RunAsync(ReportDefinition definition, CancellationToken cancellationToken = default);
}

internal sealed class ReportService : IReportService
{
    private readonly CanReportingOptions _options;
    private readonly IServiceProvider _services;
    private readonly TimeProvider _timeProvider;

    public ReportService(IEnumerable<IReportDataSource> sources, CanReportingOptions options, IServiceProvider services, TimeProvider? timeProvider = null)
    {
        DataSources = sources.ToArray();
        _options = options;
        _services = services;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public IReadOnlyList<IReportDataSource> DataSources { get; }

    public IReportDataSource? Find(string name) => DataSources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public Task<ReportResult> RunAsync(ReportDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        IReportDataSource source = (string.IsNullOrWhiteSpace(definition.DataSource) ? null : Find(definition.DataSource))
            ?? throw new ReportDefinitionException($"'{definition.DataSource}' veri kaynağı yok.");

        var engine = new ReportEngine(new ReportEngineOptions
        {
            Culture = _options.Culture ?? CultureInfo.CurrentCulture,
            TimeProvider = _timeProvider,
            MaxCells = _options.MaxCells,
            NullLabel = _options.NullLabel,
            OthersLabel = _options.OthersLabel,
            GrandTotalLabel = _options.GrandTotalLabel,
            TrueLabel = _options.TrueLabel,
            FalseLabel = _options.FalseLabel,
        });
        return source.RunAsync(definition, engine, _services, cancellationToken);
    }
}

/// <summary>Veri kaynaklarının kaydı.</summary>
public sealed class CanReportingBuilder
{
    internal CanReportingBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    /// <summary>
    /// Sorgu üzerinde veri kaynağı (scoped servislerden alınır: DbContext, tenant filtresi dahil).
    /// </summary>
    /// <example>
    /// <code>
    /// .AddSource("sales", sp =&gt; sp.GetRequiredService&lt;NorthwindDbContext&gt;().OrderLines.AsNoTracking(), o =&gt; o
    ///     .Field("unitPrice", "Birim fiyat", "C2")
    ///     .Hide("id", "tenantId"))
    /// </code>
    /// </example>
    public CanReportingBuilder AddSource<T>(string name, Func<IServiceProvider, IQueryable<T>> query, Action<ReportSourceOptions>? configure = null)
        where T : class
    {
        var options = new ReportSourceOptions();
        configure?.Invoke(options);
        var source = new QueryableReportSource<T>(name, query, options);
        Services.AddSingleton<IReportDataSource>(source);
        return this;
    }

    /// <summary>Kendi veri kaynağın (API, saklı yordam, dosya ...).</summary>
    public CanReportingBuilder AddSource(IReportDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Services.AddSingleton(source);
        return this;
    }
}

public static class ReportingServiceCollectionExtensions
{
    /// <summary>Raporlama: <see cref="IReportService"/> ve veri kaynakları.</summary>
    public static CanReportingBuilder AddCanReporting(this IServiceCollection services, Action<CanReportingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.FirstOrDefault(d => d.ServiceType == typeof(CanReportingOptions))?.ImplementationInstance as CanReportingOptions;
        if (options is null)
        {
            options = new CanReportingOptions();
            services.AddSingleton(options);
        }

        configure?.Invoke(options);
        services.TryAddScoped<IReportService>(sp => new ReportService(
            sp.GetServices<IReportDataSource>(),
            sp.GetRequiredService<CanReportingOptions>(),
            sp,
            sp.GetService<TimeProvider>()));
        return new CanReportingBuilder(services);
    }
}
