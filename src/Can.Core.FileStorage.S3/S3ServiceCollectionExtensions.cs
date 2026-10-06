using Microsoft.Extensions.DependencyInjection;

namespace Can.Core.FileStorage.S3;

public static class S3ServiceCollectionExtensions
{
    /// <summary>Dosyaları S3 uyumlu bir depoya yazar (tenant yalıtımı varsayılan olarak açık).</summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanS3FileStorage(o =&gt; builder.Configuration.GetSection("Storage:S3").Bind(o));
    /// // dotnet user-secrets set "Storage:S3:SecretAccessKey" "..."
    /// </code>
    /// </example>
    public static IServiceCollection AddCanS3FileStorage(
        this IServiceCollection services,
        Action<S3FileStorageOptions> configure,
        Action<FileStorageOptions>? configureStorage = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new S3FileStorageOptions();
        configure(options);
        options.Validate();

        const string clientName = "Can.Core.FileStorage.S3";
        services.AddHttpClient(clientName, http => http.Timeout = options.Timeout);

        return services.AddCanFileStorage(
            sp => new S3FileStorage(sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName), options, sp.GetService<TimeProvider>()),
            configureStorage
        );
    }
}
