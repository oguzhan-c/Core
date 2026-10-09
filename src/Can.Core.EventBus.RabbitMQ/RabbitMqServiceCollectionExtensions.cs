using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Can.Core.EventBus.RabbitMQ;

public static class RabbitMqServiceCollectionExtensions
{
    /// <summary>
    /// Event bus taşıyıcısını RabbitMQ yapar: yayın topic exchange'e, tüketim servis kuyruğundan (arka plan servisi).
    /// <c>AddCanEventBus(...)</c>'tan sonra çağır.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanEventBus(typeof(OrderShipped).Assembly);
    /// builder.Services.AddCanInbox&lt;AppDbContext&gt;();          // tekrar teslimleri ayıkla (önerilen)
    /// builder.Services.AddCanRabbitMqTransport(o =&gt;
    /// {
    ///     builder.Configuration.GetSection("EventBus:RabbitMQ").Bind(o);   // ConnectionString user-secrets'ta
    ///     o.ConsumerName = "billing";
    /// });
    /// </code>
    /// </example>
    public static IServiceCollection AddCanRabbitMqTransport(this IServiceCollection services, Action<RabbitMqOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new RabbitMqOptions();
        configure?.Invoke(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
            throw new InvalidOperationException("RabbitMQ ConnectionString boş.");

        services.RemoveAll<RabbitMqOptions>();
        services.AddSingleton(options);
        services.TryAddSingleton<RabbitMqConnection>();
        services.TryAddSingleton<RabbitMqTopology>();
        services.AddCanEventTransport<RabbitMqEventTransport>();
        if (options.EnableConsumer)
            services.AddSingleton<IHostedService, RabbitMqConsumerService>();
        return services;
    }
}
