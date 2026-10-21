using Microsoft.Extensions.DependencyInjection;

namespace CanFx.FileStorage.AzureBlob;

public static class AzureBlobServiceCollectionExtensions
{
    /// <summary>Dosyaları Azure Blob Storage'a yazar (tenant yalıtımı varsayılan olarak açık).</summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanAzureBlobFileStorage(o =&gt;
    /// {
    ///     o.ConnectionString = builder.Configuration["Storage:Azure:ConnectionString"]; // user-secrets / Key Vault
    ///     o.Container = "files";
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCanAzureBlobFileStorage(
        this IServiceCollection services,
        Action<AzureBlobFileStorageOptions> configure,
        Action<FileStorageOptions>? configureStorage = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new AzureBlobFileStorageOptions();
        configure(options);
        _ = options.Resolve(); // ayar hataları açılışta görünsün

        const string clientName = "CanFx.FileStorage.AzureBlob";
        services.AddHttpClient(clientName, http => http.Timeout = options.Timeout);

        return services.AddCanFileStorage(
            sp => new AzureBlobFileStorage(sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName), options, sp.GetService<TimeProvider>()),
            configureStorage);
    }
}
