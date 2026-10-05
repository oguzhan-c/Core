using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Mailing.MailKit;

public static class MailKitServiceCollectionExtensions
{
    /// <summary>SMTP ile e-posta göndermeyi kaydeder. Ayarlar hatalıysa uygulama açılırken hata verir.</summary>
    /// <example>
    /// <code>
    /// services.AddCanMailKit(o =&gt; builder.Configuration.GetSection("Mailing:Smtp").Bind(o));
    /// </code>
    /// </example>
    public static IServiceCollection AddCanMailKit(this IServiceCollection services, Action<SmtpOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SmtpOptions();
        configure(options);
        options.Validate();

        services.RemoveAll<IEmailSender>();
        services.AddSingleton<IEmailSender>(new MailKitEmailSender(options));

        return services;
    }

    /// <summary>Geliştirme için: e-postaları klasöre <c>.eml</c> olarak yazar.</summary>
    public static IServiceCollection AddCanEmailPickupDirectory(
        this IServiceCollection services,
        string directory,
        string fromAddress = "no-reply@localhost",
        string? fromName = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<IEmailSender>();
        services.AddSingleton<IEmailSender>(new PickupDirectoryEmailSender(directory, new EmailAddress(fromAddress, fromName)));

        return services;
    }
}
