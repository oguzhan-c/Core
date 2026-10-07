using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Sms;

/// <summary>Sağlayıcı seçimi.</summary>
public sealed class CanSmsBuilder
{
    internal CanSmsBuilder(IServiceCollection services) => Services = services;

    public IServiceCollection Services { get; }

    /// <summary>Testler: mesajlar <see cref="InMemorySmsProvider"/>'da tutulur.</summary>
    public CanSmsBuilder UseInMemory()
    {
        Replace();
        Services.AddSingleton<InMemorySmsProvider>();
        Services.AddSingleton<ISmsProvider>(sp => sp.GetRequiredService<InMemorySmsProvider>());
        return this;
    }

    /// <summary>Geliştirme: mesajlar klasöre JSON olarak yazılır.</summary>
    public CanSmsBuilder UsePickupDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Replace();
        Services.AddSingleton<ISmsProvider>(sp => new PickupDirectorySmsProvider(directory, sp.GetService<TimeProvider>()));
        return this;
    }

    /// <summary>HTTP API'li sağlayıcı (<see cref="HttpSmsProvider"/>): kendi HttpClient'ı ile kaydedilir.</summary>
    /// <param name="configureClient">HttpClient ayarları (temel adres, zaman aşımı, başlıklar).</param>
    /// <returns>HttpClient kurucusu: dayanıklılık eklemek için <c>.AddCanStandardResilienceHandler()</c>.</returns>
    public IHttpClientBuilder UseHttpProvider<TProvider>(Action<HttpClient>? configureClient = null)
        where TProvider : HttpSmsProvider
    {
        Replace();
        IHttpClientBuilder client = Services.AddHttpClient<TProvider>(http =>
        {
            http.Timeout = TimeSpan.FromSeconds(15);
            configureClient?.Invoke(http);
        });
        Services.AddTransient<ISmsProvider>(sp => sp.GetRequiredService<TProvider>());
        return client;
    }

    /// <summary>Başka bir sağlayıcı (HTTP dışı, ya da kendi yaşam süresiyle).</summary>
    public CanSmsBuilder UseProvider(Func<IServiceProvider, ISmsProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Replace();
        Services.AddSingleton(factory);
        return this;
    }

    private void Replace() => Services.RemoveAll<ISmsProvider>();
}

public static class SmsServiceCollectionExtensions
{
    /// <summary>
    /// SMS gönderimi. Sağlayıcı seçilmezse SMS'ler bellekte tutulur (gönderilmez) — canlıda mutlaka bir sağlayıcı seç.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.Services.AddCanSms(o =&gt; { o.DefaultSender = "NORTHWIND"; o.TransliterateToGsm = true; })
    ///     .UsePickupDirectory("sms");   // geliştirme
    /// </code>
    /// </example>
    public static CanSmsBuilder AddCanSms(this IServiceCollection services, Action<SmsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new SmsOptions();
        configure?.Invoke(options);
        if (options.MaxSegments < 1)
            throw new InvalidOperationException("MaxSegments en az 1 olmalı.");

        services.RemoveAll<SmsOptions>();
        services.AddSingleton(options);
        services.TryAddTransient<ISmsSender, SmsSender>();

        var builder = new CanSmsBuilder(services);
        if (!services.Any(d => d.ServiceType == typeof(ISmsProvider)))
            builder.UseInMemory();
        return builder;
    }
}
