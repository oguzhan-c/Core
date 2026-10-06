using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Can.Core.Mailing.MailKit;

/// <summary>SMTP sunucusuna bağlanıp (kullanıcı adı varsa) giriş yapar; e-posta göndermez.</summary>
public sealed class SmtpHealthCheck : IHealthCheck
{
    private readonly SmtpOptions _options;

    public SmtpHealthCheck(SmtpOptions options) => _options = options;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var client = new SmtpClient
        {
            Timeout = (int)Math.Min(_options.Timeout.TotalMilliseconds, 10_000),
            CheckCertificateRevocation = _options.CheckCertificateRevocation,
        };

        SecureSocketOptions socketOptions = _options.Security switch
        {
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };

        await client.ConnectAsync(_options.Host, _options.Port, socketOptions, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(_options.UserName))
            await client.AuthenticateAsync(_options.UserName, _options.Password ?? string.Empty, cancellationToken).ConfigureAwait(false);

        await client.DisconnectAsync(quit: true, cancellationToken).ConfigureAwait(false);
        return HealthCheckResult.Healthy();
    }
}

public static class SmtpHealthCheckExtensions
{
    /// <summary>
    /// SMTP kontrolü ekler (<c>AddCanMailKit</c> gerekir). E-posta kritik değilse <c>HealthStatus.Degraded</c> ver:
    /// SMTP çökse de uygulama trafik almaya devam eder.
    /// </summary>
    public static IHealthChecksBuilder AddCanSmtpCheck(
        this IHealthChecksBuilder builder,
        string name = "smtp",
        HealthStatus failureStatus = HealthStatus.Degraded,
        params string[] tags)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddCheck<SmtpHealthCheck>(name, failureStatus, tags.Length > 0 ? tags : new[] { "ready" }, TimeSpan.FromSeconds(15));
    }
}
