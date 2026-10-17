using Can.Core.EventBus;
using Can.Core.EventBus.RabbitMQ;
using Can.Core.Persistence.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CanAppTemplate.Persistence.Contexts;

namespace CanAppTemplate.Infrastructure.Messaging;

/// <summary>
/// Event bus: outbox'taki entegrasyon event'leri bus'a verilir. <c>EventBus:RabbitMq:ConnectionString</c> doluysa
/// RabbitMQ (servisler arası, inbox ile tam bir kez işleme); yoksa süreç içi taşıyıcı.
/// </summary>
public static class EventBusServiceRegistration
{
    /// <returns>Gerçek bir broker'a (RabbitMQ) bağlıysa <see langword="true"/>.</returns>
    public static bool AddEventBus(this IServiceCollection services, IConfiguration config)
    {
        services.AddCanEventBus(typeof(CanAppTemplate.Domain.Entities.AppUser).Assembly, typeof(CanAppTemplate.Application.ApplicationServiceRegistration).Assembly);

        if (config["EventBus:RabbitMq:ConnectionString"] is not { Length: > 0 })
            return false;

        services.AddCanInbox<BaseDbContext>();
        services.AddCanRabbitMqTransport(o => config.GetSection("EventBus:RabbitMq").Bind(o));
        return true;
    }
}
