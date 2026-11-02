using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CanFx.Persistence.Database;

/// <summary>Uygulama başlarken veritabanına ne yapılacağı (appsettings: <c>Database:StartupMode</c>).</summary>
public enum DatabaseStartupMode
{
    /// <summary>Bekleyen migration'ları uygular; migration yoksa şemayı oluşturur (veri korunur).</summary>
    Migrate = 0,

    /// <summary>
    /// Veritabanını silip güncel entity'lerden yeniden oluşturur (her çalıştırmada temiz şema + seed). Entity tasarlarken
    /// kullanılır. Yalnızca Development'ta ve yerel veritabanında çalışır.
    /// </summary>
    Recreate = 1,

    /// <summary>Veritabanına dokunmaz (migration'lar ayrı bir adımda uygulanıyorsa).</summary>
    None = 2,
}

/// <summary>Başlangıçta veritabanını <see cref="DatabaseStartupMode"/>'a göre hazırlar.</summary>
public static class DatabaseStartup
{
    /// <summary>
    /// Veritabanını hazırlar. <see cref="DatabaseStartupMode.Recreate"/> Development dışında ya da yerel olmayan bir
    /// veritabanında asla çalışmaz: veri silmek yerine açıklayıcı bir hatayla durur.
    /// </summary>
    /// <param name="db">Hazırlanacak veritabanının DbContext'i.</param>
    /// <param name="mode">Başlangıç modu.</param>
    /// <param name="isDevelopment">Ortam Development mı (<c>IHostEnvironment.IsDevelopment()</c>).</param>
    /// <param name="logger">İsteğe bağlı: hatırlatmalar için.</param>
    /// <param name="cancellationToken">İptal.</param>
    public static async Task PrepareAsync(
        DbContext db,
        DatabaseStartupMode mode,
        bool isDevelopment,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);

        switch (mode)
        {
            case DatabaseStartupMode.None:
                return;

            case DatabaseStartupMode.Recreate:
                EnsureRecreateAllowed(db, isDevelopment);
                await db.Database.EnsureDeletedAsync(cancellationToken).ConfigureAwait(false);
                await CreateOrMigrateAsync(db, cancellationToken).ConfigureAwait(false);
                if (HasMigrations(db))
                {
                    logger?.LogWarning(
                        "Veritabanı silinip yeniden oluşturuldu (Database:StartupMode = Recreate). Projede migration'lar var: "
                        + "veriyi korumak için Database:StartupMode = Migrate yap.");
                }
                else
                {
                    logger?.LogInformation("Veritabanı silinip güncel entity'lerden yeniden oluşturuldu (Database:StartupMode = Recreate).");
                }

                return;

            default:
                await CreateOrMigrateAsync(db, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    private static Task CreateOrMigrateAsync(DbContext db, CancellationToken cancellationToken) =>
        HasMigrations(db) ? db.Database.MigrateAsync(cancellationToken) : db.Database.EnsureCreatedAsync(cancellationToken);

    private static bool HasMigrations(DbContext db) => db.Database.IsRelational() && db.Database.GetMigrations().Any();

    private static void EnsureRecreateAllowed(DbContext db, bool isDevelopment)
    {
        if (!isDevelopment)
        {
            throw new InvalidOperationException(
                "Database:StartupMode = Recreate yalnızca Development ortamında kullanılabilir; veritabanı silinmedi. "
                + "Bu ortamda Migrate ya da None kullan.");
        }

        if (!IsLocal(db))
        {
            throw new InvalidOperationException(
                "Database:StartupMode = Recreate yalnızca yerel veritabanında (localhost, docker, SQLite) çalışır; bağlantı dizesi "
                + "başka bir sunucuyu gösteriyor, veritabanı silinmedi.");
        }
    }

    private static bool IsLocal(DbContext db)
    {
        string provider = db.Database.ProviderName ?? string.Empty;
        if (provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) || provider.Contains("InMemory", StringComparison.OrdinalIgnoreCase))
            return true;

        return db.Database.IsRelational() && IsLocalConnectionString(db.Database.GetConnectionString());
    }

    /// <summary>Bağlantı dizesindeki sunucu bu makine ya da yerel docker mı.</summary>
    internal static bool IsLocalConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return false;

        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (string key in new[] { "Host", "Server", "Data Source", "Address", "Addr", "Network Address" })
        {
            if (!builder.TryGetValue(key, out object? value) || value is not string { Length: > 0 } server)
                continue;

            // "tcp:localhost,1433", "localhost:5432", "localhost,localhost2" (ilk sunucu esas)
            string host = server.Trim();
            if (host.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
                host = host[4..];
            host = host.Split(',')[0].Trim();
            if (host.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase))
                return true;
            int colon = host.LastIndexOf(':');
            if (colon > 0 && !host.Contains("::", StringComparison.Ordinal))
                host = host[..colon];
            host = host.Split('\\')[0];

            return host.ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" or "[::1]" or "." or "(local)" or "host.docker.internal";
        }

        return false;
    }
}
