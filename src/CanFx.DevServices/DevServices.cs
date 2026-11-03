using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CanFx.DevServices;

/// <summary>Bir geliştirme servisinin nasıl sağlanacağı.</summary>
public enum DevServiceMode
{
    /// <summary>Bilgisayarda çalışıyorsa onu kullan, yoksa docker'da başlat.</summary>
    Auto = 0,

    /// <summary>Her zaman docker'da başlat.</summary>
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
/// Servis kodda <see cref="Add"/> ile de tanımlanabilir; o zaman ayardaki girdi yalnızca modu verir.
/// </summary>
public sealed class DevServicesOptions
{
    /// <summary>Kapalıysa hiçbir şey yapılmaz.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>docker-compose dosyası. Boşsa uygulama klasöründen yukarı doğru <c>docker-compose.yml</c> / <c>compose.yaml</c> aranır.</summary>
    public string? ComposeFile { get; set; }

    /// <summary>Docker'da başlatılan servisin hazır olması için en fazla beklenecek süre.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Servis adı → ayarları.</summary>
    public Dictionary<string, DevServiceSettings> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kodda tanımlanan servisler.</summary>
    public List<DevServiceDefinition> Definitions { get; } = [];

    /// <summary>Adreslerin (<see cref="DevServiceSettings.AddressFrom"/>) ve koşulların okunduğu yapılandırma.</summary>
    internal IConfiguration? Configuration { get; set; }

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

/// <summary>Ayardaki adresten sunucu ve port: bağlantı dizesi, URL ya da <c>sunucu:port</c>.</summary>
internal static class DevServiceAddress
{
    private static readonly Dictionary<string, int> SchemePorts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["amqp"] = 5672, ["amqps"] = 5671, ["redis"] = 6379, ["rediss"] = 6380, ["postgres"] = 5432,
        ["postgresql"] = 5432, ["mongodb"] = 27017, ["nats"] = 4222, ["http"] = 80, ["https"] = 443,
    };

    // Sunucunun yazıldığı anahtar → varsayılan port (Host: Npgsql, diğerleri: SQL Server).
    private static readonly (string Key, int DefaultPort)[] ServerKeys =
        [("Host", 5432), ("Server", 1433), ("Data Source", 1433), ("Address", 1433), ("Addr", 1433), ("Network Address", 1433)];

    private static readonly string[] FileExtensions = [".db", ".sqlite", ".sqlite3", ".mdf"];

    /// <summary>Ayarın değeri; değer yoksa <c>Host</c> / <c>Port</c> alanlı bölüm (<c>Mail:Smtp</c>) <c>sunucu:port</c> olur.</summary>
    public static string? Read(IConfiguration configuration, string key)
    {
        if (configuration[key] is { } value)
            return value;
        IConfigurationSection section = configuration.GetSection(key);
        return section["Host"] is { Length: > 0 } host && section["Port"] is { Length: > 0 } port ? $"{host}:{port}" : null;
    }

    /// <summary><c>Anahtar=Değer</c> (büyük/küçük harf duyarsız eşit) ya da <c>Anahtar</c> (boş değil).</summary>
    public static bool Holds(IConfiguration configuration, string? condition)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return true;
        int equals = condition.IndexOf('=', StringComparison.Ordinal);
        return equals < 0
            ? !string.IsNullOrWhiteSpace(configuration[condition.Trim()])
            : string.Equals(configuration[condition[..equals].Trim()], condition[(equals + 1)..].Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLocal(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        value = value.Trim();

        if (value.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0)
                return false;
            host = uri.Host.Trim('[', ']');
            port = uri.Port > 0 ? uri.Port : SchemePorts.GetValueOrDefault(uri.Scheme);
            return port > 0;
        }

        // Redis biçimi: "localhost:6379,abortConnect=false" → ilk uç; ilk parçada "=" varsa bağlantı dizesi.
        string first = value.Split(',')[0];
        return first.Contains('=', StringComparison.Ordinal)
            ? TryParseConnectionString(value, out host, out port)
            : TrySplit(first, defaultPort: 0, out host, out port);
    }

    private static bool TryParseConnectionString(string value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        var builder = new DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = value;
        }
        catch (ArgumentException)
        {
            return false;
        }

        foreach ((string key, int defaultPort) in ServerKeys)
        {
            if (!builder.TryGetValue(key, out object? raw) || raw?.ToString() is not { Length: > 0 } server)
                continue;
            if (FileExtensions.Any(e => server.EndsWith(e, StringComparison.OrdinalIgnoreCase)) || server.Contains('/', StringComparison.Ordinal)
                || server.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
                return false; // dosya (SQLite) ya da LocalDB: başlatılacak servis yok

            server = server.Split(',', 2) is [var first, var second] && int.TryParse(second, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                ? $"{first}:{second}"                       // SQL Server: "localhost,1433"
                : server.Split(',')[0];                     // Npgsql: birden çok sunucu → ilki
            if (server.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
                server = server[4..];
            server = server.Split('\\')[0];                // "localhost\SQLEXPRESS"
            if (server is "." or "(local)")
                server = "localhost";

            int fallback = builder.TryGetValue("Port", out object? portValue)
                && int.TryParse(portValue?.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int explicitPort) ? explicitPort : defaultPort;
            return TrySplit(server, fallback, out host, out port);
        }

        return false;
    }

    private static bool TrySplit(string value, int defaultPort, out string host, out int port)
    {
        host = value.Trim();
        port = defaultPort;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && !host.StartsWith('[') && host.IndexOf(':') == colon
            && int.TryParse(host[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            port = parsed;
            host = host[..colon];
        }

        return host.Length > 0 && port is > 0 and <= 65535;
    }
}

/// <summary>Komut çalıştırıcı (testlerde değiştirilir).</summary>
internal interface ICommandRunner
{
    Task<(int ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"{fileName} başlatılamadı.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        Task<string> output = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (-1, $"{fileName} {timeout.TotalSeconds:0} saniyede bitmedi.");
        }

        return (process.ExitCode, (await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false)));
    }
}

/// <summary>Servisleri hazırlar: yerelde varsa kullanır, yoksa docker-compose ile başlatır.</summary>
internal sealed partial class DevServicesRunner(
    DevServicesOptions options,
    IHostEnvironment environment,
    ICommandRunner commands,
    ILogger<DevServicesRunner> logger)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled || !environment.IsDevelopment())
            return;

        List<DevServiceDefinition> definitions = [.. options.Definitions, .. FromSettings()];
        string? composeFile = null;
        bool? dockerAvailable = null;

        foreach (DevServiceDefinition service in definitions)
        {
            DevServiceMode mode = options.ModeOf(service.Name);
            if (mode == DevServiceMode.Off)
                continue;

            bool running = mode != DevServiceMode.Docker && await IsReachableAsync(service.Host, service.Port, cancellationToken).ConfigureAwait(false);
            if (running)
            {
                LogLocal(service.Name, service.Host, service.Port);
                continue;
            }

            if (mode == DevServiceMode.Local)
            {
                LogLocalUnreachable(service.Name, service.Host, service.Port);
                continue;
            }

            composeFile ??= FindComposeFile();
            if (composeFile is null)
            {
                LogNoComposeFile(service.Name, environment.ContentRootPath);
                continue;
            }

            dockerAvailable ??= await IsDockerAvailableAsync(composeFile, cancellationToken).ConfigureAwait(false);
            if (dockerAvailable == false)
            {
                LogNoDocker(service.Name, service.Host, service.Port);
                continue;
            }

            await StartInDockerAsync(service, composeFile, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>appsettings'teki servisler (Compose'u olanlar; kodda tanımlananlar ayardan yalnızca modu alır).</summary>
    internal IEnumerable<DevServiceDefinition> FromSettings()
    {
        foreach ((string name, DevServiceSettings settings) in options.Services)
        {
            if (settings.Mode == DevServiceMode.Off || options.Definitions.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (string.IsNullOrWhiteSpace(settings.Compose))
            {
                LogNoCompose(name);
                continue;
            }

            IConfiguration? configuration = options.Configuration;
            if (configuration is not null && !DevServiceAddress.Holds(configuration, settings.When))
            {
                LogSkipped(name, $"koşul sağlanmadı ({settings.When})");
                continue;
            }

            string? address = settings.Address
                ?? (configuration is not null && !string.IsNullOrWhiteSpace(settings.AddressFrom) ? DevServiceAddress.Read(configuration, settings.AddressFrom) : null);
            if (string.IsNullOrWhiteSpace(address))
            {
                LogSkipped(name, settings.AddressFrom is null ? "adres yok" : $"{settings.AddressFrom} boş");
                continue;
            }

            if (!DevServiceAddress.TryParse(address, out string host, out int port))
            {
                LogBadAddress(name, settings.AddressFrom ?? nameof(DevServiceSettings.Address));
                continue;
            }

            if (!DevServiceAddress.IsLocal(host))
            {
                LogRemote(name, host, port);
                continue;
            }

            yield return new DevServiceDefinition(name, settings.Compose, host, port);
        }
    }

    private async Task StartInDockerAsync(DevServiceDefinition service, string composeFile, CancellationToken cancellationToken)
    {
        LogStarting(service.Name, service.ComposeService);
        (int exitCode, string output) = await commands.RunAsync(
            "docker",
            ["compose", "-f", composeFile, "up", "-d", "--wait", service.ComposeService],
            Path.GetDirectoryName(composeFile)!,
            options.StartTimeout,
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0)
        {
            LogDockerFailed(service.Name, service.ComposeService, output.Trim());
            return;
        }

        // Konteyner hazır görünse de port birkaç saniye sonra açılabilir.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + options.StartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsReachableAsync(service.Host, service.Port, cancellationToken).ConfigureAwait(false))
            {
                LogStarted(service.Name, service.Host, service.Port);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        LogNotReady(service.Name, service.Host, service.Port);
    }

    private async Task<bool> IsDockerAvailableAsync(string composeFile, CancellationToken cancellationToken)
    {
        try
        {
            (int exitCode, _) = await commands.RunAsync("docker", ["compose", "version"], Path.GetDirectoryName(composeFile)!, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            return exitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // docker kurulu değil
        }
    }

    internal static async Task<bool> IsReachableAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private string? FindComposeFile()
    {
        if (!string.IsNullOrWhiteSpace(options.ComposeFile))
        {
            string path = Path.IsPathRooted(options.ComposeFile) ? options.ComposeFile : Path.Combine(environment.ContentRootPath, options.ComposeFile);
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }

        for (DirectoryInfo? directory = new(environment.ContentRootPath); directory is not null; directory = directory.Parent)
        {
            foreach (string name in new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" })
            {
                string candidate = Path.Combine(directory.FullName, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Service}: başlatılmadı, {Reason}.")]
    private partial void LogSkipped(string service, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service}: Compose (docker-compose'daki servis adı) yazılmamış; servis başlatılmadı.")]
    private partial void LogNoCompose(string service);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service}: {Setting} değerinden sunucu ve port okunamadı (bağlantı dizesi, URL ya da sunucu:port olmalı).")]
    private partial void LogBadAddress(string service, string setting);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Service}: {Host}:{Port} bu bilgisayar değil; docker başlatılmaz.")]
    private partial void LogRemote(string service, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Service}: bilgisayardaki kurulum kullanılıyor ({Host}:{Port}).")]
    private partial void LogLocal(string service, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service}: {Host}:{Port} adresine bağlanılamadı (DevServices modu Local). Servisi başlat ya da modu Auto yap.")]
    private partial void LogLocalUnreachable(string service, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service}: docker-compose dosyası bulunamadı ({ContentRoot} ve üst klasörleri); servis başlatılmadı.")]
    private partial void LogNoComposeFile(string service, string contentRoot);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service}: {Host}:{Port} adresinde çalışmıyor ve docker kullanılamıyor. Docker Desktop'ı aç ya da servisi bilgisayarına kur.")]
    private partial void LogNoDocker(string service, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Service}: docker'da başlatılıyor ({ComposeService}) ...")]
    private partial void LogStarting(string service, string composeService);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Service}: docker'da çalışıyor ({Host}:{Port}).")]
    private partial void LogStarted(string service, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service}: docker compose başlatamadı ({ComposeService}): {Output}")]
    private partial void LogDockerFailed(string service, string composeService, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service}: konteyner başladı ama {Host}:{Port} süre içinde hazır olmadı.")]
    private partial void LogNotReady(string service, string host, int port);
}

public static class DevServicesExtensions
{
    /// <summary>
    /// Geliştirme servislerini appsettings'ten kaydeder (<c>DevServices</c> bölümü; bilinmeyen anahtar hata verir).
    /// Adresler <see cref="DevServiceSettings.AddressFrom"/>'daki ayardan, yalnızca Development'ta açılışta okunur.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanDevServices(builder.Configuration);
    ///
    /// var app = builder.Build();
    /// await app.Services.StartCanDevServicesAsync();   // veritabanı hazırlanmadan ÖNCE
    /// </code>
    /// </example>
    public static IServiceCollection AddCanDevServices(this IServiceCollection services, IConfiguration configuration, Action<DevServicesOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddCanDevServices(o =>
        {
            configuration.GetSection("DevServices").Bind(o, b => b.ErrorOnUnknownConfiguration = true);
            o.Configuration = configuration;
            configure?.Invoke(o);
        });
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
    ///
    /// var app = builder.Build();
    /// await app.Services.StartCanDevServicesAsync();   // veritabanı hazırlanmadan ÖNCE
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
