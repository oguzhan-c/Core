using System.Runtime.CompilerServices;

namespace Can.Core.FileStorage;

public sealed class LocalFileStorageOptions
{
    /// <summary>Dosyaların kök klasörü. Göreli yol uygulama klasörüne göredir. Web kökünün (wwwroot) altına koyma.</summary>
    public string RootPath { get; set; } = "storage";
}

/// <summary>
/// Yerel disk (ya da paylaşılan ağ diski). Yazma önce geçici dosyaya yapılır, sonra yerine taşınır: yarım yazılmış
/// dosya okunmaz. Birden fazla sunucu varsa tüm sunucuların aynı diski görmesi gerekir; yoksa S3 kullan.
/// </summary>
public sealed class LocalFileStorage : IFileStorage
{
    private readonly string _root;

    public LocalFileStorage(LocalFileStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootPath);

        _root = Path.GetFullPath(Path.IsPathRooted(options.RootPath) ? options.RootPath : Path.Combine(AppContext.BaseDirectory, options.RootPath));
        Directory.CreateDirectory(_root);
    }

    public async Task<StoredFileInfo> SaveAsync(string path, Stream content, FileSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        path = StoragePath.Normalize(path);
        string fullPath = FullPath(path);

        if (options?.Overwrite != true && File.Exists(fullPath))
            throw new FileAlreadyExistsException(path);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temp = $"{fullPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);

            File.Move(temp, fullPath, overwrite: options?.Overwrite == true);
        }
        catch (IOException) when (options?.Overwrite != true && File.Exists(fullPath))
        {
            throw new FileAlreadyExistsException(path); // aynı anda yazan başka istek kazandı
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        return Info(path, new FileInfo(fullPath), options?.ContentType);
    }

    public Task<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        string fullPath = FullPath(StoragePath.Normalize(path));
        Stream? stream = File.Exists(fullPath)
            ? new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true)
            : null;
        return Task.FromResult(stream);
    }

    public Task<StoredFileInfo?> GetInfoAsync(string path, CancellationToken cancellationToken = default)
    {
        path = StoragePath.Normalize(path);
        var file = new FileInfo(FullPath(path));
        return Task.FromResult(file.Exists ? Info(path, file, null) : null);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(File.Exists(FullPath(StoragePath.Normalize(path))));

    public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        string fullPath = FullPath(StoragePath.Normalize(path));
        if (!File.Exists(fullPath))
            return Task.FromResult(false);

        File.Delete(fullPath);
        return Task.FromResult(true);
    }

    public async IAsyncEnumerable<StoredFileInfo> ListAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string normalized = StoragePath.NormalizePrefix(prefix);
        string directory = normalized.Length == 0 ? _root : FullPath(normalized);
        if (!Directory.Exists(directory))
            yield break;

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.EndsWith(".tmp", StringComparison.Ordinal))
                continue;

            string relative = Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            yield return Info(relative, new FileInfo(file), null);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<Uri?> GetTemporaryUrlAsync(string path, TimeSpan expiresIn, CancellationToken cancellationToken = default) =>
        Task.FromResult<Uri?>(null);

    private string FullPath(string normalizedPath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(_root, normalizedPath.Replace('/', Path.DirectorySeparatorChar)));

        // Normalize zaten ".." reddediyor; yine de kök dışına çıkılmadığını doğrula (sembolik yol vb.).
        if (!fullPath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"Geçersiz dosya yolu: '{normalizedPath}'.", nameof(normalizedPath));

        return fullPath;
    }

    private static StoredFileInfo Info(string path, FileInfo file, string? contentType) =>
        new(path, file.Length, contentType ?? ContentTypes.FromPath(path), file.LastWriteTimeUtc);
}
