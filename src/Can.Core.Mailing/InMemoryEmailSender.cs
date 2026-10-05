using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Can.Core.Mailing;

/// <summary>Gönderilen e-postaları bellekte tutar; testlerde "şu kişiye şu kod gitti mi?" kontrolü için.</summary>
public sealed class InMemoryEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _sent = new();

    public IReadOnlyList<EmailMessage> Sent => _sent.ToArray();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        message.Validate();
        _sent.Enqueue(message);
        return Task.CompletedTask;
    }

    public void Clear() => _sent.Clear();
}

public static class MailingServiceCollectionExtensions
{
    /// <summary>E-postaları göndermek yerine bellekte tutan göndericiyi kaydeder (testler).</summary>
    public static IServiceCollection AddCanInMemoryEmail(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<InMemoryEmailSender>();
        services.RemoveAll<IEmailSender>();
        services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<InMemoryEmailSender>());

        return services;
    }
}
