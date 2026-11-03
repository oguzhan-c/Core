using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CanFx.DevServices;

/// <summary>Planı uygular: servisleri docker-compose ile başlatır, bilgisayardakileri denetler, notları loglar.</summary>
internal sealed partial class DevServicesRunner(
    DevServicesOptions options,
    IHostEnvironment environment,
    ICommandRunner commands,
    ILogger<DevServicesRunner> logger,
    DevServicesPlan? plan = null)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled || !environment.IsDevelopment())
            return;

        // Koddan kayıtta (yapılandırma yok) plan burada hazırlanır.
        DevServicesPlan current = plan is { } p && !ReferenceEquals(p, DevServicesPlan.Empty)
            ? p
            : new DevServicesPlanner(options, null, environment.ContentRootPath, commands).Plan();

        foreach ((LogLevel level, string message) in current.Messages)
            LogNote(level, message);

        foreach (DevServicePlanItem item in current.Items)
        {
            switch (item.Action)
            {
                case DevServiceAction.Running:
                    LogRunning(item.Name, item.Host, item.Port);
                    break;
                case DevServiceAction.Local:
                    if (await IsReachableAsync(item.Host, item.Port, cancellationToken).ConfigureAwait(false))
                        LogLocal(item.Name, item.Host, item.Port);
                    else
                        LogLocalUnreachable(item.Name, item.Host, item.Port);
                    break;
                case DevServiceAction.Fallback:
                    if (await IsReachableAsync(item.Host, item.Port, cancellationToken).ConfigureAwait(false))
                        LogLocal(item.Name, item.Host, item.Port);
                    else
                        LogUnavailable(item.Name, item.Host, item.Port);
                    break;
                default:
                    await StartInDockerAsync(item, current.ComposeFile!, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private async Task StartInDockerAsync(DevServicePlanItem item, string composeFile, CancellationToken cancellationToken)
    {
        LogStarting(item.Name, item.Compose, item.Port);
        List<string> arguments = ["compose", "-f", composeFile];
        if (item.OverrideFile is not null)
            arguments.AddRange(["-f", item.OverrideFile]);
        arguments.AddRange(["up", "-d", "--wait", item.Compose]);

        (int exitCode, string output) = await commands.RunAsync(
            "docker", arguments, Path.GetDirectoryName(composeFile)!, options.StartTimeout, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            LogDockerFailed(item.Name, item.Compose, output.Trim());
            return;
        }

        // Konteyner hazır görünse de port birkaç saniye sonra açılabilir.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + options.StartTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await IsReachableAsync(item.Host, item.Port, cancellationToken).ConfigureAwait(false))
            {
                LogStarted(item.Name, item.Host, item.Port);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        LogNotReady(item.Name, item.Host, item.Port);
    }

    internal static async Task<bool> IsReachableAsync(string host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    [LoggerMessage(Message = "{Message}")]
    private partial void LogNote(LogLevel level, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Service}: docker'da çalışıyor ({Host}:{Port}).")]
    private partial void LogRunning(string service, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Service}: bilgisayardaki kurulum kullanılıyor ({Host}:{Port}).")]
    private partial void LogLocal(string service, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service}: {Host}:{Port} adresine bağlanılamadı (DevServices modu Local). Servisi başlat ya da modu Auto yap.")]
    private partial void LogLocalUnreachable(string service, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service}: {Host}:{Port} adresinde çalışmıyor ve docker'da başlatılamadı (yukarıdaki uyarıya bak).")]
    private partial void LogUnavailable(string service, string host, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Service}: docker'da başlatılıyor ({ComposeService}, port {Port}) ...")]
    private partial void LogStarting(string service, string composeService, int port);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Service}: docker'da çalışıyor ({Host}:{Port}).")]
    private partial void LogStarted(string service, string host, int port);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service}: docker compose başlatamadı ({ComposeService}): {Output}")]
    private partial void LogDockerFailed(string service, string composeService, string output);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service}: konteyner başladı ama {Host}:{Port} süre içinde hazır olmadı.")]
    private partial void LogNotReady(string service, string host, int port);
}
