using Can.Core.Mailing.MailKit;
using Can.Core.Mailing.SendGrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CanAppTemplate.Infrastructure.Mailing;

/// <summary>E-posta: <c>Mail:Provider</c> = Pickup | Smtp | SendGrid (boşsa Pickup: <c>mails</c> klasörüne .eml).</summary>
public static class MailServiceRegistration
{
    public static IServiceCollection AddMail(this IServiceCollection services, IConfiguration config)
    {
        string provider = config["Mail:Provider"] is { Length: > 0 } value ? value : "Pickup";
        // TODO(can-gen): Gönderen adresini kendi alan adınla değiştir.
        string from = config["Mail:From"] ?? "no-reply@canapptemplate.local";

        switch (provider.ToUpperInvariant())
        {
            case "SMTP":
                // Mailpit (docker compose) ya da gerçek SMTP; şifre user-secrets'ta: "Mail:Smtp:Password"
                services.AddCanMailKit(o => config.GetSection("Mail:Smtp").Bind(o));
                break;
            case "SENDGRID":
                // dotnet user-secrets set "Mail:SendGrid:ApiKey" "<anahtar>"
                services.AddCanSendGrid(o => config.GetSection("Mail:SendGrid").Bind(o));
                break;
            default:
                services.AddCanEmailPickupDirectory(config["Mail:PickupDirectory"] is { Length: > 0 } dir ? dir : "mails", from, "CanAppTemplate");
                break;
        }

        return services;
    }
}
