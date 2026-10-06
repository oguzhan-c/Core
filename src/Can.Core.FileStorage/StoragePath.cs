namespace Can.Core.FileStorage;

/// <summary>Depolama yollarını doğrular ve tek biçime getirir.</summary>
public static class StoragePath
{
    /// <summary>
    /// <c>"a//b/./c.txt"</c> → <c>"a/b/c.txt"</c>. Boş, mutlak, <c>..</c> içeren, ters bölü ya da kontrol karakteri
    /// olan yolları reddeder (dizin dışına çıkma ve platform farkları engellenir).
    /// </summary>
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (path.Contains('\\', StringComparison.Ordinal) || path.Any(char.IsControl))
            throw new ArgumentException($"Geçersiz dosya yolu: '{path}'.", nameof(path));

        if (path.StartsWith('/') || (path.Length > 1 && path[1] == ':'))
            throw new ArgumentException($"Dosya yolu göreli olmalı: '{path}'.", nameof(path));

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != ".").ToArray();
        if (segments.Length == 0 || segments.Any(s => s == ".." || s.Trim().Length == 0 || s.Trim() != s))
            throw new ArgumentException($"Geçersiz dosya yolu: '{path}'.", nameof(path));

        return string.Join('/', segments);
    }

    /// <summary>Önek için: boşsa boş, değilse sonunda <c>/</c> olmayan normal yol.</summary>
    public static string NormalizePrefix(string? prefix) => string.IsNullOrWhiteSpace(prefix) ? string.Empty : Normalize(prefix);

    public static string Combine(string root, string path) => root.Length == 0 ? path : $"{root}/{path}";
}

/// <summary>Uzantıdan içerik tipi (sık kullanılanlar; bilinmeyen → <c>application/octet-stream</c>).</summary>
public static class ContentTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".png"] = "image/png", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon", [".avif"] = "image/avif",
        [".pdf"] = "application/pdf", [".json"] = "application/json", [".xml"] = "application/xml",
        [".zip"] = "application/zip", [".gz"] = "application/gzip",
        [".txt"] = "text/plain", [".csv"] = "text/csv", [".html"] = "text/html", [".css"] = "text/css", [".js"] = "text/javascript",
        [".md"] = "text/markdown",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".mp3"] = "audio/mpeg", [".mp4"] = "video/mp4", [".webm"] = "video/webm",
    };

    public const string Default = "application/octet-stream";

    public static string FromPath(string path) => Map.TryGetValue(Path.GetExtension(path), out string? type) ? type : Default;
}
