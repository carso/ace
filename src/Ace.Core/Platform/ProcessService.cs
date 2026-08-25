using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Ace.Core.Platform;

/// <summary>Structured result of an external process invocation.</summary>
/// <param name="ExitCode">Process exit code; -1 when the process could not be started.</param>
/// <param name="StandardOutput">Captured stdout.</param>
/// <param name="StandardError">Captured stderr.</param>
/// <param name="TimedOut">True when the process was killed after exceeding the timeout.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut = false)
{
    public bool Success => ExitCode == 0;
}

/// <summary>External-process abstraction (SRS §13). Used e.g. to shell out to the git CLI.</summary>
public interface IProcessService
{
    /// <summary>
    /// Runs <paramref name="fileName"/> with <paramref name="arguments"/> and captures stdout/stderr.
    /// Never throws for "program not found" — returns a non-success <see cref="ProcessResult"/> instead.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Default managed implementation of <see cref="IProcessService"/>.</summary>
public sealed class ProcessService : IProcessService
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, "Failed to start process.");
            }
        }
        catch (Win32Exception ex)
        {
            // Program not found / not executable — structured failure, never throw.
            return new ProcessResult(-1, string.Empty, ex.Message);
        }

        var stdoutTask = ReadAsync(process.StandardOutput, stdout);
        var stderrTask = ReadAsync(process.StandardError, stderr);

        using var timeoutSource = new CancellationTokenSource(timeout ?? DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            return new ProcessResult(-1, stdout.ToString(), stderr.ToString(), TimedOut: timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested);
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task ReadAsync(StreamReader reader, StringBuilder sink)
    {
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) is not null)
        {
            lock (sink)
            {
                sink.AppendLine(line);
            }
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already exited or inaccessible — nothing to do.
        }
    }
}
