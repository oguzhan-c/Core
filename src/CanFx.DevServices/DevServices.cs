using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
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
/// appsettings: <c>"DevServices": { "Enabled": true, "Services": { "Database": "Auto", "Redis": "Local" } }</c>.
/// Servisler kodda <see cref="Add"/> ile tanımlanır (adres yapılandırmadan, ör. bağlantı dizesinden okunur).
/// </summary>
public sealed class DevServicesOptions
{
    /// <summary>Kapalıysa hiçbir şey yapılmaz.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>docker-compose dosyası. Boşsa uygulama klasöründen yukarı doğru <c>docker-compose.yml</c> / <c>compose.yaml</c> aranır.</summary>
    public string? ComposeFile { get; set; }

    /// <summary>Docker'da başlatılan servisin hazır olması için en fazla beklenecek süre.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Servis adı → mod (yazılmayanlar <see cref="DevServiceMode.Auto"/>).</summary>
    public Dictionary<string, DevServiceMode> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tanımlı servisler.</summary>
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

    internal DevServiceMode ModeOf(string name) => Services.TryGetValue(name, out DevServiceMode mode) ? mode : DevServiceMode.Auto;

    internal static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        int colon = endpoint.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(endpoint[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            throw new ArgumentException($"Adres 'sunucu:port' biçiminde olmalı (ör. localhost:5432): '{endpoint}'.", nameof(endpoint));
        return (endpoint[..colon], port);
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
        if (!options.Enabled || !environment.IsDevelopment() || options.Definitions.Count == 0)
            return;

        string? composeFile = null;
        bool? dockerAvailable = null;

        foreach (DevServiceDefinition service in options.Definitions)
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
    /// Geliştirme servislerini kaydeder. Servisler <paramref name="configure"/> içinde tanımlanır; modlar appsettings'ten.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanDevServices(o =&gt;
    /// {
    ///     builder.Configuration.GetSection("DevServices").Bind(o);
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
