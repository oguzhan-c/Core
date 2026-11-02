using System.Text.Json;

namespace CanFx.Persistence.Seeding;

/// <summary>
/// Seed verisini koddan ayrı bir JSON dosyasından okur (çok satırlı başlangıç verisi: iller, para birimleri, kategoriler).
/// Dosya projede <c>Seeding/Data/*.json</c> olarak durur ve çıktı klasörüne kopyalanır
/// (<c>&lt;None Update="Seeding\Data\*.json" CopyToOutputDirectory="PreserveNewest" /&gt;</c>).
/// </summary>
/// <example>
/// <code>
/// // Seeding/Data/colors.json: [ { "name": "Kırmızı" }, { "name": "Mavi" } ]
/// public sealed record ColorSeed(string Name);
///
/// foreach (ColorSeed item in await SeedFiles.ReadJsonAsync&lt;ColorSeed&gt;("Seeding/Data/colors.json", ct))
///     db.Colors.Add(Color.Create(item.Name));
/// </code>
/// </example>
public static class SeedFiles
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>JSON dizisini okur. Göreli yol uygulama klasörüne göredir; dosya yoksa açıklayıcı hata verir.</summary>
    public static async Task<IReadOnlyList<T>> ReadJsonAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException(
                $"Seed dosyası bulunamadı: {fullPath}. Dosyanın projede olduğundan ve çıktıya kopyalandığından "
                + "(CopyToOutputDirectory) emin ol.", fullPath);
        }

        await using FileStream stream = File.OpenRead(fullPath);
        try
        {
            return await JsonSerializer.DeserializeAsync<List<T>>(stream, Options, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Seed dosyası okunamadı ({fullPath}): {exception.Message}", exception);
        }
    }
}
