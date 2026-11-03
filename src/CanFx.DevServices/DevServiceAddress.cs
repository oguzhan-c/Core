using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace CanFx.DevServices;

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

    /// <summary>Adresi yeni portla yazar (biçimi korunur): bağlantı dizesi, URL ya da <c>sunucu:port</c>.</summary>
    public static string WithPort(string value, int port)
    {
        value = value.Trim();
        string portText = port.ToString(CultureInfo.InvariantCulture);
        if (value.Contains("://", StringComparison.Ordinal))
            return new UriBuilder(value) { Port = port }.Uri.ToString();

        string[] parts = value.Split(',');
        if (!parts[0].Contains('=', StringComparison.Ordinal))
        {
            // "localhost:6379,abortConnect=false"
            if (!TrySplit(parts[0], 0, out string host, out _))
                return value;
            parts[0] = $"{host}:{portText}";
            return string.Join(',', parts);
        }

        var builder = new DbConnectionStringBuilder { ConnectionString = value };
        if (!TryParseConnectionString(value, out string server, out _))
            return value;
        if (builder.ContainsKey("Host"))
        {
            builder["Host"] = server;   // "localhost:5432" biçimindeyse port Port'a taşınır
            builder["Port"] = portText;
        }
        else
        {
            string key = ServerKeys.Select(k => k.Key).First(builder.ContainsKey);
            builder[key] = $"{server},{portText}";   // SQL Server: "localhost,1433"
            builder.Remove("Port");
        }

        return builder.ConnectionString;
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
