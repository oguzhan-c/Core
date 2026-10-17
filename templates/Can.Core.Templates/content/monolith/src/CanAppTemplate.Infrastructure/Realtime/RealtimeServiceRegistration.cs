using Can.Core.Realtime.SignalR;
#if (Redis)
using Can.Core.Redis.ScaleOut;
#endif
using Microsoft.Extensions.DependencyInjection;

namespace CanAppTemplate.Infrastructure.Realtime;

/// <summary>Anlık bildirim (SignalR); Redis varsa sunucular arası backplane.</summary>
public static class RealtimeServiceRegistration
{
    public static IServiceCollection AddRealtime(this IServiceCollection services, string? redis)
    {
        var signalR = services.AddCanSignalR();
#if (Redis)
        if (redis is not null)
            signalR.AddCanRedisBackplane();
#else
        _ = redis;
        _ = signalR;
#endif
        return services;
    }
}
