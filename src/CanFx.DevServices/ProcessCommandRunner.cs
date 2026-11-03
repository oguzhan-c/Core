using System.Diagnostics;

namespace CanFx.DevServices;

/// <summary>Komut çalıştırıcı (testlerde değiştirilir).</summary>
internal interface ICommandRunner
{
    Task<(int ExitCode, string Output)> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<(int ExitCode, string Output)> RunAsync(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"{fileName} başlatılamadı.");
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        Task<string> output = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return (-1, $"{fileName} {timeout.TotalSeconds:0} saniyede bitmedi.");
        }

        return (process.ExitCode, (await output.ConfigureAwait(false)) + (await error.ConfigureAwait(false)));
    }
}
