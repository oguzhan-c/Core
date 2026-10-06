using System.Runtime.CompilerServices;
using Can.Core.MultiTenancy;

namespace Can.Core.FileStorage;

public sealed class FileStorageOptions
{
    /// <summary>
    /// Açıksa (varsayılan) her yol aktif tenant'ın klasörüne yönlenir: <c>tenants/{tenantId}/...</c>; tenant yoksa
    /// <c>host/...</c>. Bir tenant diğerinin dosyasını adını bilse bile okuyamaz.
    /// </summary>
    public bool TenantIsolation { get; set; } = true;

    public string TenantsFolder { get; set; } = "tenants";

    public string HostFolder { get; set; } = "host";
}

/// <summary>Yolları aktif tenant'ın klasörüne yönlendiren sarmalayıcı (scoped).</summary>
internal sealed class TenantScopedFileStorage : IFileStorage
{
    private readonly IFileStorage _inner;
    private readonly string _root;

    public TenantScopedFileStorage(IFileStorage inner, TenantContext? tenantContext, FileStorageOptions options)
    {
        _inner = inner;
        _root = tenantContext?.TenantId is { Length: > 0 } tenantId
            ? $"{options.TenantsFolder}/{StoragePath.Normalize(tenantId)}"
            : options.HostFolder;
    }

    public async Task<StoredFileInfo> SaveAsync(string path, Stream content, FileSaveOptions? options = null, CancellationToken cancellationToken = default) =>
        Strip(await _inner.SaveAsync(Scope(path), content, options, cancellationToken).ConfigureAwait(false));

    public Task<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default) => _inner.OpenReadAsync(Scope(path), cancellationToken);

    public async Task<StoredFileInfo?> GetInfoAsync(string path, CancellationToken cancellationToken = default) =>
        await _inner.GetInfoAsync(Scope(path), cancellationToken).ConfigureAwait(false) is { } info ? Strip(info) : null;

    public Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default) => _inner.ExistsAsync(Scope(path), cancellationToken);

    public Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default) => _inner.DeleteAsync(Scope(path), cancellationToken);

    public async IAsyncEnumerable<StoredFileInfo> ListAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        string scoped = StoragePath.Combine(_root, StoragePath.NormalizePrefix(prefix));
        await foreach (StoredFileInfo info in _inner.ListAsync(scoped, cancellationToken).ConfigureAwait(false))
            yield return Strip(info);
    }

    public Task<Uri?> GetTemporaryUrlAsync(string path, TimeSpan expiresIn, CancellationToken cancellationToken = default) =>
        _inner.GetTemporaryUrlAsync(Scope(path), expiresIn, cancellationToken);

    private string Scope(string path) => StoragePath.Combine(_root, StoragePath.Normalize(path));

    private StoredFileInfo Strip(StoredFileInfo info) =>
        info.Path.StartsWith(_root + "/", StringComparison.Ordinal) ? info with { Path = info.Path[(_root.Length + 1)..] } : info;
}
