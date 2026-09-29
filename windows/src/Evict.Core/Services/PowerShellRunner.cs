using System.Text;
using System.Text.Json;

namespace Evict.Core.Services;

/// <summary>
/// Runs Windows PowerShell 5.1 (always present on Windows 10/11) with an encoded command and
/// returns the output. Scripts are asked to emit UTF-8 JSON so the result can be parsed reliably.
/// </summary>
public static class PowerShellRunner
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string PowerShellPath
    {
        get
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var p = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
            return File.Exists(p) ? p : "powershell.exe";
        }
    }

    public static bool IsAvailable
    {
        get
        {
            try { return File.Exists(PowerShellPath) || PowerShellPath == "powershell.exe"; }
            catch { return false; }
        }
    }

    /// <summary>Prefix that switches console output to UTF-8 and hides progress bars.</summary>
    private const string Prelude =
        "$ErrorActionPreference='Continue'; $ProgressPreference='SilentlyContinue'; " +
        "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; ";

    public static async Task<ProcessResult> RunScriptAsync(string script, CancellationToken ct, TimeSpan? timeout = null, Action<string>? onLine = null)
    {
        var full = Prelude + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        var args = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand {encoded}";
        return await ProcessRunner.RunCapturedAsync(PowerShellPath, args, ct, timeout ?? TimeSpan.FromMinutes(5), onLine).ConfigureAwait(false);
    }

    /// <summary>Runs a script that ends with ConvertTo-Json and deserialises the result. Returns default on failure.</summary>
    public static async Task<(T? Value, string? Error)> RunJsonAsync<T>(string script, CancellationToken ct, TimeSpan? timeout = null)
    {
        var res = await RunScriptAsync(script, ct, timeout).ConfigureAwait(false);
        var text = ExtractJson(res.StdOut);
        if (string.IsNullOrWhiteSpace(text))
        {
            var err = string.IsNullOrWhiteSpace(res.StdErr) ? $"PowerShell exited with code {res.ExitCode} and produced no output." : res.StdErr.Trim();
            return (default, err);
        }
        try
        {
            return (JsonSerializer.Deserialize<T>(text, JsonOptions), string.IsNullOrWhiteSpace(res.StdErr) ? null : res.StdErr.Trim());
        }
        catch (JsonException ex)
        {
            return (default, "Could not parse PowerShell output: " + ex.Message);
        }
    }

    /// <summary>Finds the first '[' or '{' and returns from there – strips any warnings printed before the JSON.</summary>
    internal static string ExtractJson(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return "";
        int a = stdout.IndexOf('[');
        int o = stdout.IndexOf('{');
        int start = a < 0 ? o : o < 0 ? a : Math.Min(a, o);
        if (start < 0) return "";
        var s = stdout[start..].Trim();
        // Trim trailing garbage after the last closing bracket.
        int end = Math.Max(s.LastIndexOf(']'), s.LastIndexOf('}'));
        return end >= 0 ? s[..(end + 1)] : s;
    }

    /// <summary>Escapes a string for use inside single quotes in PowerShell.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
}
