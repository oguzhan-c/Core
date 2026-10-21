using CanFx.Sms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CanAppTemplate.Infrastructure.Sms;

/// <summary>SMS: geliştirmede her mesaj <c>sms</c> klasörüne JSON olarak yazılır.</summary>
/// <remarks>
/// TODO(can-gen): Gerçek SMS sağlayıcısını bağla: bu klasöre <c>ISmsSender</c>'ı uygulayan bir istemci yaz
/// (ör. <c>NetgsmSmsSender</c>) ve <c>Sms:Provider</c>'a göre burada seç; anahtarlar user-secrets'ta.
/// </remarks>
public static class SmsServiceRegistration
{
    public static IServiceCollection AddSms(this IServiceCollection services, IConfiguration config)
    {
        services
            .AddCanSms(o =>
            {
                o.DefaultSender = "APP";
                o.TransliterateToGsm = true;
                o.MaxSegments = 3;
            })
            .UsePickupDirectory(config["Sms:PickupDirectory"] is { Length: > 0 } dir ? dir : "sms");
        return services;
    }
}
