using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CanFx.DevServices;

/// <summary>Bir servis için açılışta yapılacak iş.</summary>
internal enum DevServiceAction
{
    /// <summary>Docker'da başlat (<see cref="DevServicePlanItem.OverrideFile"/> varsa farklı portla).</summary>
    Docker,

    /// <summary>Bu projenin konteyneri zaten çalışıyor.</summary>
    Running,

    /// <summary>Bilgisayardakini kullan (mod Local).</summary>
    Local,

    /// <summary>Docker kullanılamıyor: bilgisayarda çalışıyorsa onu kullan, yoksa uyar.</summary>
    Fallback,
}

internal sealed record DevServicePlanItem(string Name, string Compose, string Host, int Port, DevServiceAction Action, string? OverrideFile = null);

/// <summary>Kayıt sırasında (Development) hazırlanan plan: servisler, port değişiklikleri ve loglanacak notlar.</summary>
internal sealed class DevServicesPlan
{
    public static readonly DevServicesPlan Empty = new();

    public string? ComposeFile { get; set; }

    public List<DevServicePlanItem> Items { get; } = [];

    /// <summary>Port değiştiyse uygulamanın okuduğu adres (yapılandırma anahtarı → yeni değer).</summary>
    public Dictionary<string, string?> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(LogLevel Level, string Message)> Messages { get; } = [];

    internal void Note(LogLevel level, string message) => Messages.Add((level, message));
}

internal sealed record PortMapping(int Target, int Published, string? HostIp, string Protocol);

/// <summary>
/// Servislerin portunu seçer: bu projenin konteyneri çalışıyorsa onun portu; değilse ayardaki port boşsa o, doluysa
/// sıradaki boş port (adres yapılandırmada yeni portla değiştirilir, docker-compose'a geçici bir ek dosyayla verilir).
/// </summary>
internal sealed class DevServicesPlanner(DevServicesOptions options, IConfiguration? configuration, string contentRoot, ICommandRunner commands)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);

    private sealed record Candidate(string Name, string Compose, string Host, int Port, DevServiceMode Mode, string? OverrideKey, Func<int, string>? Rewrite);

    public DevServicesPlan Plan()
    {
        var plan = new DevServicesPlan();
        List<Candidate> candidates = Candidates(plan);
        if (candidates.Count == 0)
            return plan;

        bool needsDocker = candidates.Any(c => c.Mode is DevServiceMode.Auto or DevServiceMode.Docker);
        bool dockerReady = false;
        Dictionary<string, List<PortMapping>>? declared = null;
        Dictionary<string, List<PortMapping>> running = new(StringComparer.Ordinal);
        if (needsDocker)
        {
            plan.ComposeFile = FindComposeFile();
            if (plan.ComposeFile is null)
                plan.Note(LogLevel.Warning, $"docker-compose dosyası bulunamadı ({contentRoot} ve üst klasörleri); servisler docker'da başlatılmaz.");
            else if (!IsDockerAvailable(plan.ComposeFile))
                plan.Note(LogLevel.Warning, "Docker kullanılamıyor (Docker Desktop açık mı?); servisler docker'da başlatılmaz.");
            else
            {
                dockerReady = true;
                declared = ReadDeclaredPorts(plan.ComposeFile);
                running = ReadRunningPorts(plan.ComposeFile);
            }
        }

        var taken = new HashSet<int>();
        foreach (Candidate c in candidates)
        {
            if (c.Mode == DevServiceMode.Local)
            {
                plan.Items.Add(new DevServicePlanItem(c.Name, c.Compose, c.Host, c.Port, DevServiceAction.Local));
                continue;
            }

            if (!dockerReady)
            {
                plan.Items.Add(new DevServicePlanItem(c.Name, c.Compose, c.Host, c.Port, DevServiceAction.Fallback));
                continue;
            }

            if (declared is not null && !declared.ContainsKey(c.Compose))
            {
                plan.Note(LogLevel.Warning, $"{c.Name}: docker-compose.yml'de '{c.Compose}' servisi yok (yorum satırında olabilir); başlatılmadı.");
                plan.Items.Add(new DevServicePlanItem(c.Name, c.Compose, c.Host, c.Port, DevServiceAction.Fallback));
                continue;
            }

            List<PortMapping> mappings = declared?.GetValueOrDefault(c.Compose) ?? [];
            PortMapping? mapping = mappings.FirstOrDefault(m => m.Published == c.Port) ?? mappings.FirstOrDefault();
            int containerPort = mapping?.Target ?? c.Port;

            int port;
            DevServiceAction action;
            if (running.GetValueOrDefault(c.Compose)?.FirstOrDefault(p => p.Target == containerPort) is { } live)
            {
                port = live.Published;
                action = DevServiceAction.Running;
            }
            else
            {
                port = c.Port;
                action = DevServiceAction.Docker;
                if (taken.Contains(port) || PortProbe.InUse(port))
                {
                    if (c.Rewrite is null)
                    {
                        // Kodda tanımlanan servis: adres değiştirilemez; Auto'da portta çalışan kullanılır.
                        if (c.Mode == DevServiceMode.Auto)
                        {
                            plan.Items.Add(new DevServicePlanItem(c.Name, c.Compose, c.Host, c.Port, DevServiceAction.Fallback));
                            continue;
                        }
                    }
                    else
                    {
                        int free = Enumerable.Range(c.Port + 1, Math.Max(options.PortRange, 1))
                            .Where(p => p <= 65535)
                            .FirstOrDefault(p => !taken.Contains(p) && !PortProbe.InUse(p));
                        if (free == 0)
                        {
                            plan.Note(LogLevel.Warning, $"{c.Name}: {c.Port} portu dolu ve sonraki {options.PortRange} portta boş yer yok; başlatılmadı.");
                            plan.Items.Add(new DevServicePlanItem(c.Name, c.Compose, c.Host, c.Port, DevServiceAction.Fallback));
                            continue;
                        }

                        plan.Note(LogLevel.Information, $"{c.Name}: {c.Port} portu başka bir programda; docker'da {free} portunda başlatılacak.");
                        port = free;
                    }
                }
            }

            if (port != c.Port && c.Rewrite is not null && c.OverrideKey is not null)
            {
                plan.Overrides[c.OverrideKey] = c.Rewrite(port);
                plan.Note(LogLevel.Information, $"{c.Name}: {c.OverrideKey} bu çalıştırmada {port} portunu kullanıyor (dosyalar değişmedi).");
            }

            string? overrideFile = action == DevServiceAction.Docker && (mapping?.Published ?? c.Port) != port
                ? WriteOverrideFile(plan.ComposeFile!, c.Compose, mappings, containerPort, port)
                : null;
            taken.Add(port);
            plan.Items.Add(new DevServicePlanItem(c.Name, c.Compose, c.Host, port, action, overrideFile));
        }

        return plan;
    }

    private List<Candidate> Candidates(DevServicesPlan plan)
    {
        var candidates = options.Definitions
            .Select(d => new Candidate(d.Name, d.ComposeService, d.Host, d.Port, options.ModeOf(d.Name), null, null))
            .Where(c => c.Mode != DevServiceMode.Off)
            .ToList();

        foreach ((string name, DevServiceSettings settings) in options.Services)
        {
            if (settings.Mode == DevServiceMode.Off || options.Definitions.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (string.IsNullOrWhiteSpace(settings.Compose))
            {
                plan.Note(LogLevel.Warning, $"{name}: Compose (docker-compose'daki servis adı) yazılmamış; servis başlatılmadı.");
                continue;
            }

            if (configuration is not null && !DevServiceAddress.Holds(configuration, settings.When))
            {
                plan.Note(LogLevel.Debug, $"{name}: başlatılmadı, koşul sağlanmadı ({settings.When}).");
                continue;
            }

            string? address = settings.Address;
            string? overrideKey = null;
            Func<int, string>? rewrite = null;
            if (string.IsNullOrWhiteSpace(address) && configuration is not null && !string.IsNullOrWhiteSpace(settings.AddressFrom))
            {
                string key = settings.AddressFrom;
                if (configuration[key] is { } value)
                {
                    address = value;
                    overrideKey = key;
                    rewrite = p => DevServiceAddress.WithPort(value, p);
                }
                else
                {
                    address = DevServiceAddress.Read(configuration, key);   // Host / Port bölümü (Mail:Smtp)
                    overrideKey = key + ":Port";
                    rewrite = p => p.ToString(CultureInfo.InvariantCulture);
                }
            }

            if (string.IsNullOrWhiteSpace(address))
            {
                plan.Note(LogLevel.Debug, $"{name}: başlatılmadı, {settings.AddressFrom ?? "adres"} boş.");
                continue;
            }

            if (!DevServiceAddress.TryParse(address, out string host, out int port))
            {
                // Dosya (SQLite), LocalDB ya da okunamayan değer: başlatılacak servis yok.
                plan.Note(LogLevel.Debug, $"{name}: {settings.AddressFrom ?? nameof(DevServiceSettings.Address)} değerinde bir sunucu portu yok; başlatılmadı.");
                continue;
            }

            if (!DevServiceAddress.IsLocal(host))
            {
                plan.Note(LogLevel.Information, $"{name}: {host}:{port} bu bilgisayar değil; docker başlatılmaz.");
                continue;
            }

            candidates.Add(new Candidate(name, settings.Compose, host, port, settings.Mode, overrideKey, rewrite));
        }

        return candidates;
    }

    private string? FindComposeFile()
    {
        if (!string.IsNullOrWhiteSpace(options.ComposeFile))
        {
            string path = Path.IsPathRooted(options.ComposeFile) ? options.ComposeFile : Path.Combine(contentRoot, options.ComposeFile);
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }

        for (DirectoryInfo? directory = new(contentRoot); directory is not null; directory = directory.Parent)
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

    private (int ExitCode, string Output) Run(string composeFile, params string[] arguments)
    {
        try
        {
            return commands.RunAsync("docker", arguments, Path.GetDirectoryName(composeFile)!, CommandTimeout, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "docker kurulu değil");
        }
    }

    private bool IsDockerAvailable(string composeFile) => Run(composeFile, "compose", "version").ExitCode == 0;

    /// <summary>
    /// <c>docker compose config</c>: servis → port eşlemeleri. Okunamazsa null (servis var sayılır). Tüm profiller açılır:
    /// yoksa profilli servisler (redis: <c>profiles: [cache, all]</c>) listede çıkmaz ve "servis yok" sanılır.
    /// </summary>
    private Dictionary<string, List<PortMapping>>? ReadDeclaredPorts(string composeFile)
    {
        (int exitCode, string output) = Run(composeFile, "compose", "-f", composeFile, "--profile", "*", "config", "--format", "json");
        return exitCode == 0 ? ParseDeclaredPorts(output) : null;
    }

    /// <summary><c>docker compose ps</c>: çalışan servis → yayınlanmış portlar.</summary>
    private Dictionary<string, List<PortMapping>> ReadRunningPorts(string composeFile)
    {
        (int exitCode, string output) = Run(composeFile, "compose", "-f", composeFile, "ps", "--format", "json");
        return exitCode == 0 ? ParseRunningPorts(output) : new(StringComparer.Ordinal);
    }

    internal static Dictionary<string, List<PortMapping>>? ParseDeclaredPorts(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("services", out JsonElement services) || services.ValueKind != JsonValueKind.Object)
                return null;
            var result = new Dictionary<string, List<PortMapping>>(StringComparer.Ordinal);
            foreach (JsonProperty service in services.EnumerateObject())
            {
                var ports = new List<PortMapping>();
                if (service.Value.TryGetProperty("ports", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement port in list.EnumerateArray())
                    {
                        if (Number(port, "target") is int target && Number(port, "published") is int published)
                            ports.Add(new PortMapping(target, published, Text(port, "host_ip"), Text(port, "protocol") ?? "tcp"));
                    }
                }

                result[service.Name] = ports;
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static Dictionary<string, List<PortMapping>> ParseRunningPorts(string output)
    {
        var result = new Dictionary<string, List<PortMapping>>(StringComparer.Ordinal);
        string trimmed = output.Trim();
        if (trimmed.Length == 0)
            return result;

        // Yeni sürümler satır başına bir nesne, eskiler bir dizi yazar.
        IEnumerable<string> documents = trimmed.StartsWith('[') ? new[] { trimmed } : trimmed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string text in documents)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                IEnumerable<JsonElement> containers = document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement.EnumerateArray().ToList()
                    : new[] { document.RootElement };
                foreach (JsonElement container in containers)
                {
                    if (Text(container, "Service") is not { } service || !string.Equals(Text(container, "State"), "running", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!container.TryGetProperty("Publishers", out JsonElement publishers) || publishers.ValueKind != JsonValueKind.Array)
                        continue;
                    if (!result.TryGetValue(service, out List<PortMapping>? ports))
                        result[service] = ports = [];
                    foreach (JsonElement publisher in publishers.EnumerateArray())
                    {
                        if (Number(publisher, "TargetPort") is int target && Number(publisher, "PublishedPort") is int published and > 0
                            && ports.All(p => p.Target != target))
                            ports.Add(new PortMapping(target, published, Text(publisher, "URL"), Text(publisher, "Protocol") ?? "tcp"));
                    }
                }
            }
            catch (JsonException)
            {
                // satır JSON değil (uyarı vb.)
            }
        }

        return result;
    }

    /// <summary>
    /// Servisin portunu değiştiren ek compose dosyası (<c>docker compose -f ana -f ek</c>); diğer port eşlemeleri korunur.
    /// </summary>
    private static string WriteOverrideFile(string composeFile, string service, List<PortMapping> mappings, int containerPort, int port)
    {
        var lines = new List<string> { $"\"{port}:{containerPort}\"" };
        foreach (PortMapping m in mappings.Where(m => m.Target != containerPort))
        {
            string hostIp = string.IsNullOrEmpty(m.HostIp) || m.HostIp is "0.0.0.0" or "::" ? string.Empty : m.HostIp + ":";
            string protocol = string.Equals(m.Protocol, "tcp", StringComparison.OrdinalIgnoreCase) ? string.Empty : "/" + m.Protocol;
            lines.Add($"\"{hostIp}{m.Published}:{m.Target}{protocol}\"");
        }

        var yaml = new StringBuilder()
            .AppendLine("# CanFx.DevServices: port başka bir programda olduğu için geçici olarak değiştirildi.")
            .AppendLine("services:")
            .Append("  ").Append(service).AppendLine(":")
            .AppendLine("    ports: !override");
        foreach (string line in lines)
            yaml.Append("      - ").AppendLine(line);

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(composeFile)))[..12].ToLowerInvariant();
        string path = Path.Combine(Path.GetTempPath(), $"canfx-devservices-{hash}-{service}.yml");
        File.WriteAllText(path, yaml.ToString());
        return path;
    }

    private static int? Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>Port denetimi: bir program dinliyorsa ya da port bağlanamıyorsa dolu.</summary>
internal static class PortProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(300);

    public static bool InUse(int port)
    {
        if (IsReachable(IPAddress.Loopback, port))
            return true;
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Any, port));
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    private static bool IsReachable(IPAddress address, int port)
    {
        using var client = new TcpClient();
        try
        {
            return client.ConnectAsync(address, port).Wait(ConnectTimeout) && client.Connected;
        }
        catch (AggregateException)
        {
            return false;
        }
    }
}
