using global::RabbitMQ.Client;

namespace Can.Core.EventBus.RabbitMQ;

/// <summary>
/// Uygulama başına tek bağlantı (RabbitMQ önerisi: bağlantı uzun ömürlü, kanallar ucuz). Kopunca istemci kendiliğinden
/// yeniden bağlanır, kuyruk/bağlama/tüketicileri yeniden kurar.
/// </summary>
public sealed class RabbitMqConnection : IAsyncDisposable, IDisposable
{
    private readonly RabbitMqOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _publisher;

    public RabbitMqConnection(RabbitMqOptions options) => _options = options;

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
            return open;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true } existing)
                return existing;

            var factory = new ConnectionFactory
            {
                Uri = new Uri(_options.ConnectionString),
                ClientProvidedName = _options.ClientName ?? _options.ResolveConsumerName(),
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
                ConsumerDispatchConcurrency = Math.Max((ushort)1, _options.ConsumerConcurrency),
            };
            if (_connection is not null)
                await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Yayın kanalı: gönderim onayı açık; <c>BasicPublishAsync</c> broker kalıcı olarak alana kadar bekler, reddederse
    /// <c>PublishException</c> fırlatır. Onay takibi sayesinde eşzamanlı yayın güvenli.
    /// </summary>
    public async Task<IChannel> GetPublisherAsync(CancellationToken cancellationToken)
    {
        if (_publisher is { IsOpen: true } open)
            return open;

        IConnection connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_publisher is { IsOpen: true } existing)
                return existing;
            if (_publisher is not null)
                await _publisher.DisposeAsync().ConfigureAwait(false);
            _publisher = await connection
                .CreateChannelAsync(new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true), cancellationToken)
                .ConfigureAwait(false);
            return _publisher;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_publisher is not null)
            await _publisher.DisposeAsync().ConfigureAwait(false);
        if (_connection is not null)
            await _connection.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
