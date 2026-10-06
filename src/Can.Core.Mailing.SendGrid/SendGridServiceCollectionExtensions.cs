using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Mailing.SendGrid;

public static class SendGridServiceCollectionExtensions
{
    /// <summary>
    /// E-postaları SendGrid ile gönderir (<see cref="IEmailSender"/> olarak kaydedilir, önceki göndericinin yerine geçer).
    /// HttpClient, <c>IHttpClientFactory</c> ile yönetilir (bağlantı havuzu, DNS yenileme).
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanSendGrid(o =&gt; builder.Configuration.GetSection("Mail:SendGrid").Bind(o));
    /// // dotnet user-secrets set "Mail:SendGrid:ApiKey" "SG.xxxx"
    /// </code>
    /// </example>
    public static IServiceCollection AddCanSendGrid(this IServiceCollection services, Action<SendGridOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new SendGridOptions();
        configure(options);
        options.Validate();

        services.RemoveAll<IEmailSender>();
        services.AddSingleton(options);
        services.AddHttpClient<SendGridEmailSender>(http => http.Timeout = options.Timeout);
        services.AddTransient<IEmailSender>(sp => sp.GetRequiredService<SendGridEmailSender>());

        return services;
    }
}
