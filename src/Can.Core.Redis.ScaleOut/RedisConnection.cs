using StackExchange.Redis;

namespace Can.Core.Redis.ScaleOut;

public sealed class RedisScaleOutOptions
{
    /// <summary>StackExchange.Redis bağlantı metni (<c>localhost:6379</c>). Şifre içeriyorsa user-secrets / ortam değişkeni.</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Uygulamanın adı: anahtar önekleri (<c>northwind:lock:...</c>), SignalR kanal öneki ve Data Protection uygulama adı.
    /// Aynı uygulamanın tüm sunucularında aynı, farklı uygulamalarda farklı olmalı.
    /// </summary>
    public string ApplicationName { get; set; } = "can";
}

/// <summary>Paylaşılan tek Redis bağlantısı (bağlantı pahalıdır; uygulama boyunca bir tane). İlk kullanımda açılır.</summary>
public sealed class RedisConnection : IAsyncDisposable, IDisposable
{
    private readonly Lazy<Task<IConnectionMultiplexer>> _connection;

    public RedisConnection(RedisScaleOutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        _connection = new Lazy<Task<IConnectionMultiplexer>>(ConnectAsync);
    }

    /// <summary>Hazır bağlantıyla (testler, başka bir yerde açılmış bağlantı).</summary>
    public RedisConnection(RedisScaleOutOptions options, IConnectionMultiplexer connection)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connection);
        Options = options;
        _connection = new Lazy<Task<IConnectionMultiplexer>>(Task.FromResult(connection));
    }

    public RedisScaleOutOptions Options { get; }

    public Task<IConnectionMultiplexer> GetAsync() => _connection.Value;

    public async Task<IDatabase> GetDatabaseAsync() => (await GetAsync().ConfigureAwait(false)).GetDatabase();

    /// <summary>Uygulamaya özel anahtar: <c>{ApplicationName}:{suffix}</c>.</summary>
    public string Key(string suffix) => $"{Options.ApplicationName}:{suffix}";

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated)
            await (await _connection.Value.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>DI kapsayıcısı senkron dispose edildiğinde (ör. <c>using ServiceProvider</c>).</summary>
    public void Dispose()
    {
        if (_connection.IsValueCreated && _connection.Value.IsCompletedSuccessfully)
            _connection.Value.Result.Dispose();
    }

    private async Task<IConnectionMultiplexer> ConnectAsync()
    {
        ConfigurationOptions configuration = ConfigurationOptions.Parse(Options.ConnectionString);
        configuration.AbortOnConnectFail = false; // Redis geç açılırsa uygulama çökmesin; arka planda bağlanır
        configuration.ClientName ??= Options.ApplicationName;
        return await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false);
    }
}
