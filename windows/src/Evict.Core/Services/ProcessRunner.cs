using System.Diagnostics;
using System.Text;

namespace Evict.Core.Services;

public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
    public bool TimedOut { get; init; }
    public bool Success => ExitCode == 0 && !TimedOut;
}

/// <summary>Small helper around <see cref="Process"/> for captured, cancellable command execution.</summary>
public static class ProcessRunner
{
    /// <summary>Raised with the PID of every process Evict starts through this class (installer detection ignores them).</summary>
    public static event Action<int>? ProcessStarted;
    public static void NotifyStarted(int pid) { try { ProcessStarted?.Invoke(pid); } catch { /* observers must not break callers */ } }

    public static async Task<ProcessResult> RunCapturedAsync(
        string fileName,
        string arguments,
        CancellationToken ct,
        TimeSpan? timeout = null,
        Action<string>? onOutputLine = null,
        Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stdout) stdout.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr) stderr.AppendLine(e.Data);
            onOutputLine?.Invoke(e.Data);
        };

        proc.Start();
        NotifyStarted(proc.Id);
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);

        bool timedOut = false;
        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
            if (ct.IsCancellationRequested) throw;
        }

        // Make sure async readers flushed.
        try { proc.WaitForExit(); } catch { /* ignore */ }

        return new ProcessResult
        {
            ExitCode = timedOut ? -1 : SafeExitCode(proc),
            StdOut = stdout.ToString(),
            StdErr = stderr.ToString(),
            TimedOut = timedOut,
        };
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    /// <summary>Runs a command through ShellExecute (so the target's own manifest / UAC applies) and waits for it.</summary>
    public static async Task<int?> RunShellAndWaitAsync(string fileName, string arguments, string? workingDirectory, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory ?? "",
        };
        using var proc = Process.Start(psi);
        if (proc is null) return null;
        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Don't kill – killing a half-finished uninstaller is worse than letting it finish.
            throw;
        }
        return SafeExitCode(proc);
    }
}
