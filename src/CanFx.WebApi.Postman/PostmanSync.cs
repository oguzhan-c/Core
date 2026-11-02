using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CanFx.WebApi.OpenApi;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CanFx.WebApi.Postman;

/// <summary>
/// appsettings <c>"Postman"</c> bölümü. Anahtar ve çalışma alanı GİZLİDİR, appsettings'e yazılmaz:
/// <code>
/// dotnet user-secrets set "Postman:ApiKey" "PMAK-..."
/// dotnet user-secrets set "Postman:WorkspaceId" "..."
/// </code>
/// </summary>
public sealed class PostmanSyncOptions
{
    /// <summary>Kapalıysa hiçbir şey yapılmaz. Anahtar yoksa da çalışmaz.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Postman API anahtarı (Postman → Settings → API keys). Yalnızca user-secrets / ortam değişkeni.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Koleksiyon ve ortamın oluşturulacağı çalışma alanı. Boşsa anahtarın varsayılan çalışma alanı.</summary>
    public string? WorkspaceId { get; set; }

    /// <summary>Koleksiyon adı. Boşsa uygulama adı. Birden çok belge varsa sonuna belge adı eklenir (<c>Shop v2</c>).</summary>
    public string? CollectionName { get; set; }

    /// <summary>Ortam (environment) adı. Boşsa <c>{uygulama} - Development</c>.</summary>
    public string? EnvironmentName { get; set; }

    /// <summary>Ortamdaki <c>baseUrl</c>. Boşsa uygulamanın dinlediği ilk adres (http tercih edilir).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Hangi koleksiyonun güncelleneceğinin tutulduğu dosya. Boşsa kullanıcı klasöründe
    /// (<c>ApplicationData/CanFx/postman/</c>); projeye girmez, böylece her geliştiricinin kendi koleksiyonu olur.
    /// </summary>
    public string? StateFile { get; set; }
}

internal sealed partial class PostmanSyncService(
    PostmanSyncOptions options,
    CanOpenApiOptions openApi,
    IHostEnvironment environment,
    IHostApplicationLifetime lifetime,
    IServer server,
    IHttpClientFactory httpClients,
    ILogger<PostmanSyncService> logger) : IHostedService
{
    internal const string PostmanClient = "CanFx.Postman";
    internal const string SelfClient = "CanFx.Postman.Self";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled || !environment.IsDevelopment())
            return Task.CompletedTask;

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            LogNoApiKey();
            return Task.CompletedTask;
        }

        // Uygulama dinlemeye başladıktan sonra arka planda; açılışı bekletmez, hata uygulamayı durdurmaz.
        lifetime.ApplicationStarted.Register(() => _ = Task.Run(() => RunAsync(lifetime.ApplicationStopping)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            string? address = ServerAddress();
            if (address is null)
            {
                LogNoAddress();
                return;
            }

            string stateFile = StateFilePath();
            PostmanState state = await LoadStateAsync(stateFile, cancellationToken).ConfigureAwait(false);
            if (state.WorkspaceId != options.WorkspaceId)
                state = new PostmanState { WorkspaceId = options.WorkspaceId };   // çalışma alanı değişti: baştan oluştur

            var api = new PostmanApi(httpClients.CreateClient(PostmanClient));
            HttpClient self = httpClients.CreateClient(SelfClient);
            string baseName = string.IsNullOrWhiteSpace(options.CollectionName) ? environment.ApplicationName : options.CollectionName;
            var documents = openApi.Documents.Distinct(StringComparer.Ordinal).ToList();

            foreach (string document in documents)
            {
                string url = address.TrimEnd('/') + openApi.DocumentPath.Replace("{documentName}", document, StringComparison.Ordinal);
                string json = await self.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                string name = documents.Count == 1 ? baseName : $"{baseName} {document}";

                state.Documents.TryGetValue(document, out PostmanDocumentState? existing);
                (PostmanDocumentState updated, PostmanSyncResult result) = await PostmanSynchronizer
                    .SyncCollectionAsync(api, options.WorkspaceId, name, json, existing, cancellationToken).ConfigureAwait(false);
                state.Documents[document] = updated;
                await SaveStateAsync(stateFile, state, cancellationToken).ConfigureAwait(false);
                LogCollection(name, result);
            }

            string environmentName = string.IsNullOrWhiteSpace(options.EnvironmentName) ? $"{environment.ApplicationName} - Development" : options.EnvironmentName;
            var variables = new Dictionary<string, string> { ["baseUrl"] = string.IsNullOrWhiteSpace(options.BaseUrl) ? address.TrimEnd('/') : options.BaseUrl.TrimEnd('/') };
            state.EnvironmentUid = await PostmanSynchronizer
                .SyncEnvironmentAsync(api, options.WorkspaceId, environmentName, variables, state.EnvironmentUid, cancellationToken).ConfigureAwait(false);
            await SaveStateAsync(stateFile, state, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogFailed(exception, exception.Message);
        }
    }

    /// <summary>Uygulamanın dinlediği adres; joker adresler localhost'a çevrilir, http tercih edilir.</summary>
    private string? ServerAddress()
    {
        ICollection<string>? addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0)
            return null;

        string address = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? addresses.First();
        return NormalizeAddress(address);
    }

    internal static string NormalizeAddress(string address)
    {
        foreach (string wildcard in new[] { "://0.0.0.0:", "://[::]:", "://+:", "://*:" })
            address = address.Replace(wildcard, "://localhost:", StringComparison.Ordinal);
        return address.TrimEnd('/');
    }

    private string StateFilePath()
    {
        if (!string.IsNullOrWhiteSpace(options.StateFile))
            return options.StateFile;

        string project = PostmanSynchronizer.Hash(environment.ContentRootPath)[..12];
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanFx", "postman");
        return Path.Combine(folder, $"{Sanitize(environment.ApplicationName)}-{project}.json");
    }

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
            builder.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        return builder.ToString();
    }

    private static readonly JsonSerializerOptions StateJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task<PostmanState> LoadStateAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return new PostmanState();
        try
        {
            await using FileStream stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<PostmanState>(stream, StateJson, cancellationToken).ConfigureAwait(false) ?? new PostmanState();
        }
        catch (JsonException)
        {
            return new PostmanState();   // bozuk dosya: baştan oluştur
        }
    }

    internal static async Task SaveStateAsync(string path, PostmanState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, state, StateJson, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Postman eşitleme kapalı: anahtar yok. Açmak için: dotnet user-secrets set \"Postman:ApiKey\" \"PMAK-...\"")]
    private partial void LogNoApiKey();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Postman eşitleme: uygulamanın dinlediği adres bulunamadı; atlandı.")]
    private partial void LogNoAddress();

    [LoggerMessage(Level = LogLevel.Information, Message = "Postman koleksiyonu '{Collection}': {Result}.")]
    private partial void LogCollection(string collection, PostmanSyncResult result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Postman eşitleme başarısız: {Reason}")]
    private partial void LogFailed(Exception exception, string reason);
}

public static class PostmanSyncExtensions
{
    /// <summary>
    /// Development'ta, uygulama açıldıktan sonra OpenAPI belgelerini Postman'e aktarır: ilk seferde koleksiyon ve
    /// <c>baseUrl</c>'li ortam oluşturulur, sonraki açılışlarda AYNI koleksiyon güncellenir (belge değişmediyse hiçbir
    /// istek atılmaz). Ayarlar <c>"Postman"</c> bölümünden; anahtar yoksa hiçbir şey yapmaz.
    /// <c>AddCanOpenApi</c>'den SONRA çağır.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanOpenApi(o =&gt; o.Title = "Mağaza API");
    /// builder.Services.AddCanPostmanSync(builder.Configuration);
    /// </code>
    /// </example>
    public static IServiceCollection AddCanPostmanSync(this IServiceCollection services, IConfiguration configuration, Action<PostmanSyncOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!services.Any(d => d.ServiceType == typeof(CanOpenApiOptions)))
            throw new InvalidOperationException("AddCanPostmanSync için önce builder.Services.AddCanOpenApi() çağır.");

        var options = new PostmanSyncOptions();
        configuration.GetSection("Postman").Bind(options);
        configure?.Invoke(options);
        services.TryAddSingleton(options);

        services.AddHttpClient(PostmanSyncService.PostmanClient, client =>
        {
            client.BaseAddress = new Uri(PostmanApi.BaseAddress);
            client.Timeout = TimeSpan.FromSeconds(60);
            if (!string.IsNullOrWhiteSpace(options.ApiKey))
                client.DefaultRequestHeaders.Add("x-api-key", options.ApiKey);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        // Uygulamanın kendi belgesini okumak için; yalnızca localhost'ta geliştirme sertifikası hatası yok sayılır.
        services.AddHttpClient(PostmanSyncService.SelfClient)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri?.IsLoopback == true,
            });

        services.AddHostedService<PostmanSyncService>();
        return services;
    }
}
