using CanFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CanFx.FileStorage;

/// <summary>Asıl (tenant'tan habersiz) depo; <see cref="IFileStorage"/> bunun üzerine tenant yalıtımı ekler.</summary>
public sealed class FileStorageBackend(IFileStorage storage)
{
    public IFileStorage Storage { get; } = storage;
}

public static class FileStorageServiceCollectionExtensions
{
    /// <summary>Dosyaları yerel diske yazar.</summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanLocalFileStorage(o =&gt; o.RootPath = "/var/app-files");
    ///
    /// // handler
    /// await storage.SaveAsync($"products/{id}/photo.jpg", stream, new() { Overwrite = true }, ct);
    /// </code>
    /// </example>
    public static IServiceCollection AddCanLocalFileStorage(
        this IServiceCollection services,
        Action<LocalFileStorageOptions>? configure = null,
        Action<FileStorageOptions>? configureStorage = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new LocalFileStorageOptions();
        configure?.Invoke(options);
        return services.AddCanFileStorage(_ => new LocalFileStorage(options), configureStorage);
    }

    /// <summary>Depoyu kaydeder (sağlayıcı paketleri kullanır). <see cref="IFileStorage"/> scoped'dır.</summary>
    public static IServiceCollection AddCanFileStorage(
        this IServiceCollection services,
        Func<IServiceProvider, IFileStorage> backendFactory,
        Action<FileStorageOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(backendFactory);

        var options = new FileStorageOptions();
        configure?.Invoke(options);

        services.RemoveAll<FileStorageBackend>();
        services.RemoveAll<FileStorageOptions>();
        services.RemoveAll<IFileStorage>();

        services.AddSingleton(options);
        services.TryAddSingleton<IFileUploadValidator>(new FileUploadValidator());   // AddCanFileUploadValidation ile özelleştirilir
        services.AddSingleton(sp => new FileStorageBackend(backendFactory(sp)));
        services.AddScoped<IFileStorage>(sp =>
        {
            IFileStorage backend = sp.GetRequiredService<FileStorageBackend>().Storage;
            return options.TenantIsolation
                ? new TenantScopedFileStorage(backend, sp.GetService<TenantContext>(), options)
                : backend;
        });

        return services;
    }

    /// <summary>
    /// Yüklenen dosyaların varsayılan kuralları (boyut, uzantılar, içerik denetimi): <see cref="IFileUploadValidator"/>.
    /// Çağrılmazsa <see cref="FileUploadPolicy"/> varsayılanları geçerlidir.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanFileUploadValidation(p =&gt;
    /// {
    ///     p.MaxSizeBytes = 5 * 1024 * 1024;
    ///     p.AllowedExtensions = new HashSet&lt;string&gt; { ".jpg", ".png", ".pdf" };
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCanFileUploadValidation(this IServiceCollection services, Action<FileUploadPolicy>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var policy = new FileUploadPolicy();
        configure?.Invoke(policy);
        if (policy.MaxSizeBytes <= 0)
            throw new InvalidOperationException("FileUploadPolicy.MaxSizeBytes pozitif olmalı.");
        if (policy.AllowedExtensions.Count == 0)
            throw new InvalidOperationException("FileUploadPolicy.AllowedExtensions boş olamaz.");

        services.RemoveAll<IFileUploadValidator>();
        services.AddSingleton<IFileUploadValidator>(new FileUploadValidator(policy));
        return services;
    }
}
