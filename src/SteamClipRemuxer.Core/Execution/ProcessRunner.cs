using System.Diagnostics;

namespace SteamClipRemuxer.Core.Execution;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default);
}

/// <summary>
/// Runs a child process with arguments passed as a list, never as a concatenated string, so
/// paths containing quotes, spaces or filter-special characters cannot corrupt the command.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName, IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in arguments) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
                throw new ProcessLaunchException($"Failed to start '{fileName}'.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ProcessLaunchException(
                $"Could not run '{fileName}'. Ensure it is installed and on PATH.", ex);
        }

        // Both pipes must be drained before waiting. Redirecting a stream and not reading it
        // deadlocks as soon as the child fills the pipe buffer.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process ended between the check and the kill; nothing to clean up.
        }
    }
}

public sealed class ProcessLaunchException : Exception
{
    public ProcessLaunchException(string message, Exception? inner = null) : base(message, inner) { }
}
