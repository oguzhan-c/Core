using Can.Core.FileStorage;
using Can.Core.FileStorage.AzureBlob;
using Can.Core.FileStorage.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CanAppTemplate.Infrastructure.FileStorage;

/// <summary>Dosyalar: <c>Storage:Provider</c> = Local | S3 | Azure. Dosyalar varsayılan olarak tenant klasörüne yazılır.</summary>
public static class FileStorageServiceRegistration
{
    public static IServiceCollection AddFileStorage(this IServiceCollection services, IConfiguration config)
    {
        switch ((config["Storage:Provider"] ?? "Local").ToUpperInvariant())
        {
            case "S3":
                // SeaweedFS / MinIO / AWS / R2; anahtar user-secrets'ta: "Storage:S3:SecretAccessKey"
                services.AddCanS3FileStorage(o => config.GetSection("Storage:S3").Bind(o));
                break;
            case "AZURE":
                // Azurite: "UseDevelopmentStorage=true"; gerçek hesap anahtarı user-secrets'ta
                services.AddCanAzureBlobFileStorage(o => config.GetSection("Storage:Azure").Bind(o));
                break;
            default:
                services.AddCanLocalFileStorage(o => o.RootPath = config["Storage:Local:RootPath"] is { Length: > 0 } root ? root : "files");
                break;
        }

        return services;
    }
}
