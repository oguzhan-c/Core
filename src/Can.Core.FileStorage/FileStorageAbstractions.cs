namespace Can.Core.FileStorage;

/// <summary>
/// Dosya depolama. Yollar <c>/</c> ile ayrılan göreli yollardır (<c>"products/42/photo.jpg"</c>); <c>..</c>, mutlak yol
/// ve ters bölü kabul edilmez. Tenant yalıtımı açıksa (varsayılan) yollar otomatik olarak aktif tenant'ın klasörüne
/// yönlenir; uygulama kodu tenant'ı düşünmez.
/// </summary>
public interface IFileStorage
{
    /// <summary>Dosyayı yazar. <see cref="FileSaveOptions.Overwrite"/> kapalıysa ve dosya varsa <see cref="FileAlreadyExistsException"/>.</summary>
    Task<StoredFileInfo> SaveAsync(string path, Stream content, FileSaveOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>Okumak için açar; dosya yoksa <see langword="null"/>. Akışı çağıran kapatır.</summary>
    Task<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default);

    Task<StoredFileInfo?> GetInfoAsync(string path, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>Siler; dosya yoksa <see langword="false"/>.</summary>
    Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary><paramref name="prefix"/> altındaki dosyalar (alt klasörler dahil).</summary>
    IAsyncEnumerable<StoredFileInfo> ListAsync(string? prefix = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dosyayı uygulamadan geçmeden indirmek için süreli adres (S3 presigned URL). Desteklemeyen depolar
    /// (yerel disk) <see langword="null"/> döner; o zaman dosyayı bir endpoint üzerinden <see cref="OpenReadAsync"/> ile ver.
    /// </summary>
    Task<Uri?> GetTemporaryUrlAsync(string path, TimeSpan expiresIn, CancellationToken cancellationToken = default);
}

public sealed class FileSaveOptions
{
    /// <summary>Boşsa uzantıdan tahmin edilir.</summary>
    public string? ContentType { get; init; }

    public bool Overwrite { get; init; }
}

/// <summary>Depodaki dosya. <see cref="Path"/> uygulamanın verdiği yoldur (tenant klasörü olmadan).</summary>
public sealed record StoredFileInfo(string Path, long Size, string ContentType, DateTimeOffset LastModified);

public sealed class FileAlreadyExistsException(string path) : IOException($"'{path}' zaten var.")
{
    public string Path { get; } = path;
}
