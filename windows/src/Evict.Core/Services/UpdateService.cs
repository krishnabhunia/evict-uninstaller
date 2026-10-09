using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Evict.Core.Services;

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public sealed record ReleaseInfo(
    Version Version,
    string TagName,
    string Name,
    string HtmlUrl,
    string? Body,
    DateTimeOffset? PublishedAt,
    bool Prerelease,
    IReadOnlyList<ReleaseAsset> Assets)
{
    public UpdateVersion SemanticVersion => UpdateVersion.Parse(TagName) ?? new(UpdateChecker.Normalize(Version));
    public string DisplayVersion => SemanticVersion.ToString();
    public bool IsPreview => Prerelease || SemanticVersion.IsPrerelease;
}

public enum UpdateStatus { UpToDate, UpdateAvailable, Unavailable }

public sealed record UpdateCheckResult(UpdateStatus Status, Version CurrentVersion, ReleaseInfo? Release, string Message)
{
    public bool NetworkAccessBlocked { get; init; }

    public static UpdateCheckResult Unavailable(Version current, string message, bool networkAccessBlocked = false) =>
        new(UpdateStatus.Unavailable, current, null, message) { NetworkAccessBlocked = networkAccessBlocked };
}

/// <summary>
/// Pure, unit-tested logic behind the update check: version parsing/comparison, release JSON parsing,
/// asset selection and checksum parsing. No I/O.
/// </summary>
public static class UpdateChecker
{
    public const string RepoOwner = "krishnabhunia";
    // GitHub pagination uses this permanent repository identity after /repos redirects.
    public const string RepositoryId = "1372964211";
    /// <summary>Renamed from "evict" when the macOS app moved into the same repository (GitHub redirects the old name).</summary>
    public const string RepoName = "evict-uninstaller";
    public static string RepoUrl => $"https://github.com/{RepoOwner}/{RepoName}";
    public static string ReleasesUrl => RepoUrl + "/releases";
    public static string LatestApiUrl => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
    public static string ReleasesApiUrl => $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=100";
    public static string LatestRedirectUrl => ReleasesUrl + "/latest";

    /// <summary>
    /// The repository also publishes the macOS app under "mac-vX.Y.Z" tags. Windows releases use "win-vX.Y.Z"
    /// (and "vX.Y.Z" before the merge); anything tagged for macOS is never offered as a Windows update.
    /// </summary>
    public static bool IsWindowsTag(string? tag) => UpdateVersion.Parse(tag) != null;

    /// <summary>"v1.2.0", "1.2", "release-1.2.3", "v1.2.0-beta.1" → 1.2.0 (always three parts). Null when there is no version.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        int i = 0;
        while (i < s.Length && !char.IsDigit(s[i])) i++;
        if (i == s.Length) return null;
        int j = i;
        while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '.')) j++;
        var core = s[i..j].Trim('.');
        var parts = core.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        int[] n = new int[3];
        for (int k = 0; k < Math.Min(3, parts.Length); k++)
        {
            if (!int.TryParse(parts[k], out n[k]) || n[k] < 0) return null;
        }
        return new Version(n[0], n[1], n[2]);
    }

    /// <summary>Drops the revision so 1.2.0.0 (assembly) and 1.2.0 (tag) compare equal.</summary>
    public static Version Normalize(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));

    public static bool IsNewer(Version current, Version candidate) => Normalize(candidate) > Normalize(current);

    public static bool IsNewer(string current, ReleaseInfo candidate) =>
        UpdateVersion.Parse(current) is { } installed && candidate.SemanticVersion.CompareTo(installed) > 0;

    public static string CurrentVersionLabel =>
        UpdateVersion.Parse(typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)?.ToString()
        ?? CurrentVersion.ToString(3);

    /// <summary>The version of the running application (from the assembly, normalised to three parts).</summary>
    public static Version CurrentVersion
    {
        get
        {
            // Every project shares <Version> from Directory.Build.props, so the Core assembly carries the app version.
            var v = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return Normalize(v);
        }
    }

    /// <summary>Parses the JSON of GitHub's "get the latest release" (or a single release) endpoint.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        return ParseRelease(doc.RootElement);
    }

    public static List<ReleaseInfo> ParseReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("GitHub returned an invalid release list.");
        return doc.RootElement.EnumerateArray().Select(ParseRelease).OfType<ReleaseInfo>().ToList();
    }

    public static ReleaseInfo? SelectLatestRelease(IEnumerable<ReleaseInfo> releases, bool includePrereleases = false) =>
        releases.Where(r => IsWindowsTag(r.TagName) && (includePrereleases || !r.IsPreview))
            .OrderByDescending(r => r.SemanticVersion)
            .ThenByDescending(r => r.TagName.StartsWith("win-v", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(r => r.PublishedAt)
            .FirstOrDefault();

    private static ReleaseInfo? ParseRelease(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var semanticVersion = UpdateVersion.Parse(tag);
        if (semanticVersion is null || tag is null || !IsWindowsTag(tag)) return null;
        var version = semanticVersion.Core;
        bool draft = root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
        if (draft) return null;

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                long size = a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url)) assets.Add(new ReleaseAsset(name, url, size));
            }
        }

        DateTimeOffset? published = null;
        if (root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), out var dto)) published = dto;

        return new ReleaseInfo(
            version,
            tag,
            root.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String ? nm.GetString() ?? tag : tag,
            root.TryGetProperty("html_url", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() ?? ReleasesUrl : ReleasesUrl,
            root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
            published,
            semanticVersion.IsPrerelease || (root.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True),
            assets);
    }

    /// <summary>"https://github.com/o/r/releases/tag/v1.3.0" → "v1.3.0" (from the Location header of /releases/latest).</summary>
    public static string? ParseTagFromRedirect(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        const string marker = "/releases/tag/";
        int i = location.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var tag = location[(i + marker.Length)..];
        int q = tag.IndexOfAny(new[] { '?', '#', '/' });
        if (q >= 0) tag = tag[..q];
        tag = Uri.UnescapeDataString(tag).Trim();
        return tag.Length == 0 ? null : tag;
    }

    /// <summary>Builds a release from just a tag (redirect fallback) with the asset URLs GitHub uses for release downloads.</summary>
    public static ReleaseInfo? ReleaseFromTag(string tag)
    {
        var semanticVersion = UpdateVersion.Parse(tag);
        if (semanticVersion is null || !IsWindowsTag(tag)) return null;
        var v = semanticVersion.Core;
        var label = semanticVersion.ToString();
        string Dl(string name) => $"{RepoUrl}/releases/download/{Uri.EscapeDataString(tag)}/{name}";
        var assets = new List<ReleaseAsset>
        {
            new("Evict.exe", Dl("Evict.exe"), 0),
            new("Evict.exe.sha256", Dl("Evict.exe.sha256"), 0),
            new($"Evict-Setup-{label}.exe", Dl($"Evict-Setup-{label}.exe"), 0),
            new($"Evict-Setup-{label}.exe.sha256", Dl($"Evict-Setup-{label}.exe.sha256"), 0),
        };
        return new ReleaseInfo(v, tag, tag, $"{RepoUrl}/releases/tag/{Uri.EscapeDataString(tag)}", null, null, semanticVersion.IsPrerelease, assets);
    }

    /// <summary>Installed (Setup) → the Evict-Setup-*.exe; portable → the raw Evict.exe.</summary>
    public static ReleaseAsset? PickAsset(ReleaseInfo release, bool installedMode)
    {
        if (installedMode)
        {
            return release.Assets.FirstOrDefault(a => a.Name.StartsWith("Evict-Setup", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                ?? release.Assets.FirstOrDefault(a => a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }
        return release.Assets.FirstOrDefault(a => a.Name.Equals("Evict.exe", StringComparison.OrdinalIgnoreCase));
    }

    public static ReleaseAsset? PickChecksumAsset(ReleaseInfo release, ReleaseAsset asset)
        => release.Assets.FirstOrDefault(a => a.Name.Equals(asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));

    /// <summary>Extracts the first 64-hex-digit token from a checksum file ("ABC…", "abc…  Evict.exe", PowerShell output…).</summary>
    public static string? ParseSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var token in text.Split(new[] { ' ', '\t', '\r', '\n', '*', '=' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length == 64 && token.All(Uri.IsHexDigit)) return token.ToLowerInvariant();
        }
        return null;
    }

    /// <summary>Case-insensitive directory comparison tolerant to trailing separators and mixed slashes.</summary>
    public static bool SameDirectory(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        static string Norm(string s) => s.Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        return Norm(a) == Norm(b);
    }

    /// <summary>Human text for the banner: "1.2.0 → 1.3.0".</summary>
    public static string Describe(Version current, Version latest) => $"{current.ToString(3)} → {latest.ToString(3)}";
}

/// <summary>
/// Checks GitHub Releases for a newer version, downloads the matching asset only after verifying its published
/// SHA-256 checksum and applies it: installed copies run the new Setup silently, portable copies replace
/// Evict.exe in place and restart. Every network failure is reported as "unavailable", never thrown.
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient SharedHttp = CreateClient();
    private readonly HttpClient _http;
    private readonly string? _updatesDirectory;
    private readonly string? _currentVersionLabel;
    private readonly Action<ProcessStartInfo> _startProcess;
    private readonly bool _logDiagnostics = true;
    public const string OldBinaryName = "Evict.old.exe";

    public UpdateService()
    {
        _http = SharedHttp;
        _startProcess = StartProcess;
    }

    internal UpdateService(HttpClient http, string updatesDirectory, Action<ProcessStartInfo>? startProcess = null, string? currentVersionLabel = null)
    {
        _http = http;
        _updatesDirectory = updatesDirectory;
        _currentVersionLabel = currentVersionLabel;
        _startProcess = startProcess ?? StartProcess;
        _logDiagnostics = false; // Isolated transports use only their supplied workspace, including diagnostics.
    }

    private void LogInfo(string message) { if (_logDiagnostics) Log.Info(message); }
    private void LogWarn(string message) { if (_logDiagnostics) Log.Warn(message); }
    private void LogError(string message, Exception ex) { if (_logDiagnostics) Log.Error(message, ex); }

    private static void StartProcess(ProcessStartInfo info)
    {
        using var process = Process.Start(info);
        if (process is null) throw new InvalidOperationException("The updated application could not be started.");
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"Evict-Uninstaller/{UpdateChecker.CurrentVersion.ToString(3)} (+{UpdateChecker.RepoUrl})");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        c.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return c;
    }

    public string CurrentVersionLabel => _currentVersionLabel ?? UpdateChecker.CurrentVersionLabel;
    public Version CurrentVersion => UpdateVersion.Parse(CurrentVersionLabel)?.Core ?? UpdateChecker.CurrentVersion;
    public string UpdatesDir
    {
        get
        {
            var d = _updatesDirectory ?? Path.Combine(AppPaths.DataRoot, "updates");
            try { Directory.CreateDirectory(d); } catch { /* ignore */ }
            return d;
        }
    }

    // ───────────────────────────── check ─────────────────────────────

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct, bool includePrereleases = false)
    {
        var current = CurrentVersion;
        var channel = includePrereleases ? "stable and beta" : "stable";
        try
        {
            var releases = new List<ReleaseInfo>();
            string? next = UpdateChecker.ReleasesApiUrl;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (int page = 0; next != null; page++)
            {
                if (page >= 20 || !visited.Add(next))
                    return UpdateCheckResult.Unavailable(current, "The release list is incomplete. Please try again or open GitHub Releases.");
                using var response = await _http.GetAsync(next, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    var reason = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                        ? "GitHub API rate limit reached. Please try again later."
                        : response.StatusCode == HttpStatusCode.NotFound
                            ? "No published release was found."
                            : $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.";
                    return UpdateCheckResult.Unavailable(current, reason);
                }
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                releases.AddRange(UpdateChecker.ParseReleases(json));
                next = NextReleasePage(response);
            }
            var release = UpdateChecker.SelectLatestRelease(releases, includePrereleases);
            if (release is null)
                return UpdateCheckResult.Unavailable(current, $"No published Windows release is available on the {channel} channel.");
            if (UpdateChecker.IsNewer(CurrentVersionLabel, release))
                return new(UpdateStatus.UpdateAvailable, current, release, $"Version {release.DisplayVersion} is available (you have {CurrentVersionLabel}).");
            return new(UpdateStatus.UpToDate, current, release, $"No newer Windows release is available on the {channel} channel (you have {CurrentVersionLabel}).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            LogWarn("Update check failed: " + ex.Message);
            if (UpdateNetworkDiagnostics.IsSocketAccessDenied(ex))
                return UpdateCheckResult.Unavailable(current,
                    UpdateNetworkDiagnostics.PermissionDeniedMessage(ExePath), networkAccessBlocked: true);
            return UpdateCheckResult.Unavailable(current, "Could not check GitHub releases: " + (ex.InnerException?.Message ?? ex.Message));
        }
    }

    private static string? NextReleasePage(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var headers)) return null;
        foreach (var part in headers.SelectMany(h => h.Split(',')))
        {
            var relation = Regex.Match(part, @"(?:^|;)\s*rel\s*=\s*(?:""([^""]+)""|([^;""\s]+))", RegexOptions.IgnoreCase);
            if (!relation.Success)
            {
                if (Regex.IsMatch(part, @"\brel\s*=.*\bnext\b", RegexOptions.IgnoreCase))
                    throw new InvalidDataException("GitHub returned an incomplete release-list page link.");
                continue;
            }
            var names = (relation.Groups[1].Success ? relation.Groups[1].Value : relation.Groups[2].Value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!names.Contains("next", StringComparer.OrdinalIgnoreCase)) continue;
            var link = Regex.Match(part, @"<([^>]+)>");
            if (!link.Success || !Uri.TryCreate(link.Groups[1].Value, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "api.github.com" || !uri.IsDefaultPort
                || (uri.AbsolutePath != $"/repos/{UpdateChecker.RepoOwner}/{UpdateChecker.RepoName}/releases"
                    && uri.AbsolutePath != $"/repositories/{UpdateChecker.RepositoryId}/releases"))
                throw new InvalidDataException("GitHub returned an invalid release-list page link.");
            return uri.AbsoluteUri;
        }
        return null;
    }

    // ───────────────────────────── download ─────────────────────────────

    /// <summary>Downloads an asset only when its published SHA-256 checksum can be verified. Returns the local path.</summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, ReleaseAsset asset, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var target = Path.Combine(UpdatesDir, SafeFileName(asset.Name.Equals("Evict.exe", StringComparison.OrdinalIgnoreCase) ? $"Evict-{release.DisplayVersion}.exe" : asset.Name));
        var partial = target + ".partial";
        try { if (File.Exists(partial)) File.Delete(partial); } catch { /* ignore */ }

        var checksum = UpdateChecker.PickChecksumAsset(release, asset);
        if (checksum is null)
            throw new InvalidDataException("This release has no SHA-256 checksum for the update. Download a verified release from GitHub instead.");
        string? expectedHash;
        try
        {
            var text = await _http.GetStringAsync(checksum.DownloadUrl, ct).ConfigureAwait(false);
            expectedHash = UpdateChecker.ParseSha256(text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (UpdateNetworkDiagnostics.IsSocketAccessDenied(ex))
                throw new InvalidDataException(UpdateNetworkDiagnostics.PermissionDeniedMessage(ExePath), ex);
            throw new InvalidDataException("The update checksum could not be downloaded. The update was stopped; please try again.", ex);
        }
        if (expectedHash is null) throw new InvalidDataException("The published update checksum is invalid. The update was stopped.");

        try
        {
            using (var resp = await _http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? asset.Size;
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    progress?.Report((done, total));
                }
            }

            var actual = await ComputeSha256Async(partial, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The downloaded file is corrupt (SHA-256 mismatch). Please try again.");

            try { if (File.Exists(target)) File.Delete(target); } catch { /* ignore */ }
            File.Move(partial, target);
            LogInfo($"Downloaded update {asset.Name} → {target} (checksum OK)");
            return target;
        }
        catch (Exception ex)
        {
            try { File.Delete(partial); } catch { /* keep the original download failure */ }
            if (UpdateNetworkDiagnostics.IsSocketAccessDenied(ex))
                throw new InvalidDataException(UpdateNetworkDiagnostics.PermissionDeniedMessage(ExePath), ex);
            throw;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SafeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    // ───────────────────────────── apply ─────────────────────────────

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Evict.exe");
    public static string ExeDirectory => Path.GetDirectoryName(ExePath) ?? AppContext.BaseDirectory;

    /// <summary>The Setup-written InstallDir marker (HKCU for per-user installs, HKLM for all users), or null.</summary>
    public static string? InstalledDir()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = root.OpenSubKey(@"Software\Evict");
                if (k?.GetValue("InstallDir") is string s && !string.IsNullOrWhiteSpace(s)) return s;
            }
            catch { /* ignore */ }
        }
        return null;
    }

    /// <summary>True when this exe runs from the folder the installer wrote → updates go through Setup.</summary>
    public static bool IsInstalledMode() => UpdateChecker.SameDirectory(InstalledDir(), ExeDirectory);

    /// <summary>
    /// Installed: start the new Setup silently (it closes Evict, replaces the files and relaunches).
    /// Portable: rename the running Evict.exe to Evict.old.exe, move the new file in, start it.
    /// Returns true when the caller must now shut the application down.
    /// </summary>
    public (bool Ok, string? Error) Apply(string downloadedPath, bool installedMode, Action beforeRestart, Action? restartFailed = null)
    {
        bool ownershipReleased = false;
        string? RestoreOwnership()
        {
            if (!ownershipReleased || restartFailed is null) return null;
            try { restartFailed(); return null; }
            catch (Exception ex) { LogError("Could not resume after the update handoff failed", ex); return " Evict could not resume safely: " + ex.Message; }
        }
        try
        {
            if (installedMode)
            {
                beforeRestart();
                ownershipReleased = true;
                _startProcess(new ProcessStartInfo(downloadedPath, "/SILENT /CLOSEAPPLICATIONS /NORESTART /EVICTUPDATE=1")
                {
                    UseShellExecute = true, // honours the installer's own UAC request (all-users installs)
                    WorkingDirectory = Path.GetDirectoryName(downloadedPath) ?? UpdatesDir,
                });
                LogInfo("Started installer for update: " + downloadedPath);
                return (true, null);
            }

            var exe = ExePath;
            var dir = ExeDirectory;
            var old = Path.Combine(dir, OldBinaryName);
            try { if (File.Exists(old)) File.Delete(old); } catch { /* an older instance may still hold it */ }
            if (File.Exists(old)) old = Path.Combine(dir, $"Evict.old.{Environment.ProcessId}.exe");

            File.Move(exe, old);                 // renaming a running exe is allowed on Windows
            try { File.Move(downloadedPath, exe); }
            catch
            {
                try { File.Move(old, exe); } catch { /* nothing more we can do */ }
                throw;
            }

            beforeRestart();
            ownershipReleased = true;
            _startProcess(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true, WorkingDirectory = dir });
            LogInfo($"Replaced {exe} (previous build kept as {Path.GetFileName(old)} until next start).");
            return (true, null);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogError("Applying update failed", ex);
            return (false, $"No permission to replace {ExePath}. Restart Evict as administrator or download the new version manually from {UpdateChecker.ReleasesUrl}." + RestoreOwnership());
        }
        catch (Exception ex)
        {
            LogError("Applying update failed", ex);
            return (false, ex.Message + RestoreOwnership());
        }
    }

    /// <summary>Called at start-up: removes Evict.old.exe left by a portable self-update and stale downloads.</summary>
    public void CleanupAfterUpdate()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(ExeDirectory, "Evict.old*.exe"))
            {
                try { File.Delete(f); LogInfo("Removed previous build " + f); } catch { /* still locked – next time */ }
            }
        }
        catch { /* ignore */ }
        try
        {
            var dir = Path.Combine(AppPaths.DataRoot, "updates");
            if (Directory.Exists(dir))
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-7) || f.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)) File.Delete(f); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }
    }
}
