using System.Text;
using Can.Core.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.FileStorage.Tests;

public sealed class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "can-files-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static MemoryStream Text(string value) => new(Encoding.UTF8.GetBytes(value));

    private static async Task<string> ReadAsync(IFileStorage storage, string path)
    {
        await using Stream? stream = await storage.OpenReadAsync(path);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task Save_read_info_list_and_delete()
    {
        var storage = new LocalFileStorage(new LocalFileStorageOptions { RootPath = _root });

        StoredFileInfo saved = await storage.SaveAsync("docs/2026/rapor.pdf", Text("pdf"));
        await storage.SaveAsync("docs/not.txt", Text("merhaba"));

        Assert.Equal("docs/2026/rapor.pdf", saved.Path);
        Assert.Equal(3, saved.Size);
        Assert.Equal("application/pdf", saved.ContentType);
        Assert.Equal("merhaba", await ReadAsync(storage, "docs/not.txt"));
        Assert.True(await storage.ExistsAsync("docs/not.txt"));
        Assert.Equal(7, (await storage.GetInfoAsync("docs/not.txt"))?.Size);

        string[] listed = await storage.ListAsync("docs").Select(f => f.Path).ToArrayAsync();
        Assert.Equal(new[] { "docs/2026/rapor.pdf", "docs/not.txt" }, listed);

        Assert.True(await storage.DeleteAsync("docs/not.txt"));
        Assert.False(await storage.DeleteAsync("docs/not.txt"));
        Assert.Null(await storage.OpenReadAsync("docs/not.txt"));
        Assert.Null(await storage.GetTemporaryUrlAsync("docs/2026/rapor.pdf", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Existing_file_is_not_overwritten_unless_asked()
    {
        var storage = new LocalFileStorage(new LocalFileStorageOptions { RootPath = _root });
        await storage.SaveAsync("a.txt", Text("1"));

        await Assert.ThrowsAsync<FileAlreadyExistsException>(() => storage.SaveAsync("a.txt", Text("2")));
        await storage.SaveAsync("a.txt", Text("3"), new FileSaveOptions { Overwrite = true });

        Assert.Equal("3", await ReadAsync(storage, "a.txt"));
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("a/../../b.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/windows/x")]
    [InlineData("a\\b.txt")]
    [InlineData(" ")]
    [InlineData("a/ b /c")]
    public void Unsafe_paths_are_rejected(string path)
    {
        Assert.ThrowsAny<ArgumentException>(() => StoragePath.Normalize(path));
    }

    [Fact]
    public void Paths_are_normalized()
    {
        Assert.Equal("a/b/c.txt", StoragePath.Normalize("a//b/./c.txt/"));
    }

    [Fact]
    public async Task Tenants_get_isolated_folders()
    {
        var services = new ServiceCollection();
        services.AddCanMultiTenancy();
        services.AddCanLocalFileStorage(o => o.RootPath = _root);
        await using ServiceProvider provider = services.BuildServiceProvider();

        async Task SaveAsync(string? tenant, string text)
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            if (tenant is not null)
                scope.ServiceProvider.GetRequiredService<TenantContext>().Set(tenant);
            StoredFileInfo info = await scope.ServiceProvider.GetRequiredService<IFileStorage>().SaveAsync("logo.png", Text(text));
            Assert.Equal("logo.png", info.Path); // uygulama tenant klasörünü görmez
        }

        await SaveAsync("t-a", "A");
        await SaveAsync("t-b", "B");
        await SaveAsync(null, "H");

        Assert.True(File.Exists(Path.Combine(_root, "tenants", "t-a", "logo.png")));
        Assert.True(File.Exists(Path.Combine(_root, "tenants", "t-b", "logo.png")));
        Assert.True(File.Exists(Path.Combine(_root, "host", "logo.png")));

        await using AsyncServiceScope scopeA = provider.CreateAsyncScope();
        scopeA.ServiceProvider.GetRequiredService<TenantContext>().Set("t-a");
        IFileStorage storageA = scopeA.ServiceProvider.GetRequiredService<IFileStorage>();

        Assert.Equal("A", await ReadAsync(storageA, "logo.png"));
        Assert.Equal(new[] { "logo.png" }, await storageA.ListAsync().Select(f => f.Path).ToArrayAsync());
    }
}
