using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace CanFx.DevServices;

/// <summary>Bir geliştirme servisinin nasıl sağlanacağı.</summary>
public enum DevServiceMode
{
    /// <summary>
    /// docker-compose'dan başlat: bu projenin konteyneri çalışıyorsa onu kullan; port başka bir programdaysa sıradaki boş
    /// portta başlat (uygulamanın adresi o çalıştırma için güncellenir). Docker yoksa bilgisayarda çalışanı kullan.
    /// </summary>
    Auto = 0,

    /// <summary><see cref="Auto"/> gibi; docker yoksa bilgisayardakini kullanmaz, yalnızca uyarır.</summary>
    Docker = 1,

    /// <summary>Her zaman bilgisayardakini kullan (docker'a dokunma); erişilemiyorsa yalnızca uyar.</summary>
    Local = 2,

    /// <summary>Hiçbir şey yapma.</summary>
    Off = 3,
}

/// <summary>Bir geliştirme servisi: adı (ayarlardaki), docker-compose'daki servis adı ve uygulamanın bağlandığı adres.</summary>
public sealed record DevServiceDefinition(string Name, string ComposeService, string Host, int Port);

/// <summary>
/// appsettings'teki bir servis: <c>"Redis": { "Compose": "redis", "AddressFrom": "Redis:ConnectionString" }</c>.
/// </summary>
public sealed class DevServiceSettings
{
    /// <summary>Nasıl sağlanacağı.</summary>
    public DevServiceMode Mode { get; set; } = DevServiceMode.Auto;

    /// <summary>docker-compose.yml'deki servis adı (ör. <c>postgres</c>).</summary>
    public string? Compose { get; set; }

    /// <summary>
    /// Uygulamanın bağlandığı adresin okunduğu ayar (ör. <c>ConnectionStrings:App</c>, <c>Redis:ConnectionString</c>,
    /// <c>Mail:Smtp</c>). Değer bağlantı dizesi, URL, <c>sunucu:port</c> ya da <c>Host</c> / <c>Port</c> alanları olan bir
    /// bölüm olabilir. Değer boşsa servis başlatılmaz (uygulama yerel karşılığını kullanıyordur).
    /// </summary>
    public string? AddressFrom { get; set; }

    /// <summary>Adres doğrudan (<see cref="AddressFrom"/> yerine): <c>localhost:5432</c>, URL ya da bağlantı dizesi.</summary>
    public string? Address { get; set; }

    /// <summary>Yalnızca bu koşulda: <c>Mail:Provider=Smtp</c> (değer eşitse) ya da <c>Redis:ConnectionString</c> (boş değilse).</summary>
    public string? When { get; set; }
}

/// <summary>
/// appsettings (Development): servisler, docker-compose'daki adları ve adreslerinin okunduğu ayarla birlikte yazılır;
/// kod gerekmez:
/// <code>
/// "DevServices": {
///   "Services": {
///     "Database": { "Compose": "postgres", "AddressFrom": "ConnectionStrings:App" },
///     "Redis": { "Compose": "redis", "AddressFrom": "Redis:ConnectionString", "Mode": "Local" }
///   }
/// }
/// </code>
/// Port doluysa (başka bir program, ör. bilgisayardaki PostgreSQL) servis sıradaki boş portta başlatılır ve uygulamanın
/// okuduğu adres (<see cref="DevServiceSettings.AddressFrom"/>) o çalıştırma için yeni portla değiştirilir; dosyalara
/// dokunulmaz. Servis kodda <see cref="Add"/> ile de tanımlanabilir; o zaman ayardaki girdi yalnızca modu verir (port
/// değiştirilemez).
/// </summary>
public sealed class DevServicesOptions
{
    /// <summary>Kapalıysa hiçbir şey yapılmaz.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>docker-compose dosyası. Boşsa uygulama klasöründen yukarı doğru <c>docker-compose.yml</c> / <c>compose.yaml</c> aranır.</summary>
    public string? ComposeFile { get; set; }

    /// <summary>Docker'da başlatılan servisin hazır olması için en fazla beklenecek süre.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Servisin portu doluysa sonraki kaç port denenir (5432 doluysa 5433, 5434 ...).</summary>
    public int PortRange { get; set; } = 100;

    /// <summary>Servis adı → ayarları.</summary>
    public Dictionary<string, DevServiceSettings> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kodda tanımlanan servisler.</summary>
    public List<DevServiceDefinition> Definitions { get; } = [];

    /// <summary>Servis tanımlar. <paramref name="endpoint"/>: <c>localhost:5432</c> biçiminde adres.</summary>
    public DevServicesOptions Add(string name, string composeService, string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(composeService);
        (string host, int port) = ParseEndpoint(endpoint);
        Definitions.Add(new DevServiceDefinition(name, composeService, host, port));
        return this;
    }

    internal DevServiceMode ModeOf(string name) => Services.TryGetValue(name, out DevServiceSettings? settings) ? settings.Mode : DevServiceMode.Auto;

    internal static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            throw new ArgumentException($"Adres 'sunucu:port' biçiminde olmalı (ör. localhost:5432): '{endpoint}'.", nameof(endpoint));
        return (endpoint[..colon], port);
    }
}

public static class DevServicesExtensions
{
    /// <summary>
    /// Geliştirme servislerini appsettings'ten kaydeder (<c>DevServices</c> bölümü; bilinmeyen anahtar hata verir).
    /// Development'ta portlar burada seçilir: dolu bir portun yerine kullanılacak port yapılandırmaya yazılır (yalnızca
    /// bellekte). Bu yüzden adresleri okuyan kayıtlardan ÖNCE çağrılmalı.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanDevServices(builder.Configuration, builder.Environment);   // ilk kayıtlardan biri
    ///
    /// var app = builder.Build();
    /// await app.Services.StartCanDevServicesAsync();   // veritabanı hazırlanmadan ÖNCE
    /// </code>
    /// </example>
    public static IServiceCollection AddCanDevServices(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment, Action<DevServicesOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var options = new DevServicesOptions();
        configuration.GetSection("DevServices").Bind(options, b => b.ErrorOnUnknownConfiguration = true);
        configure?.Invoke(options);

        DevServicesPlan plan = DevServicesPlan.Empty;
        var commands = new ProcessCommandRunner();
        if (options.Enabled && environment.IsDevelopment())
        {
            plan = new DevServicesPlanner(options, configuration, environment.ContentRootPath, commands).Plan();
            if (plan.Overrides.Count > 0)
            {
                if (configuration is IConfigurationBuilder builder)
                    builder.AddInMemoryCollection(plan.Overrides);
                else
                {
                    foreach ((string key, string? value) in plan.Overrides)
                        configuration[key] = value;
                }
            }
        }

        services.TryAddSingleton(options);
        services.TryAddSingleton(plan);
        services.TryAddSingleton<ICommandRunner>(commands);
        services.TryAddSingleton<DevServicesRunner>();
        return services;
    }

    /// <summary>
    /// Geliştirme servislerini koddan kaydeder: servisler <paramref name="configure"/> içinde <see cref="DevServicesOptions.Add"/> ile.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanDevServices(o =&gt;
    /// {
    ///     o.Add("Database", composeService: "postgres", endpoint: "localhost:5432");
    ///     o.Add("Redis", composeService: "redis", endpoint: "localhost:6379");
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCanDevServices(this IServiceCollection services, Action<DevServicesOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new DevServicesOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.TryAddSingleton<ICommandRunner, ProcessCommandRunner>();
        services.TryAddSingleton<DevServicesRunner>();
        return services;
    }

    /// <summary>Servisleri hazırlar (yalnızca Development'ta). Uygulamayı hiçbir durumda durdurmaz; sorunları loglar.</summary>
    public static Task StartCanDevServicesAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.GetService<DevServicesRunner>() is { } runner ? runner.StartAsync(cancellationToken) : Task.CompletedTask;
    }
}
