using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Evict.Core.Services;

/// <summary>Which releases the version picker lists (design VP1).</summary>
public enum VersionFilter { All, Stable, Beta }

/// <summary>
/// Pure rules behind the version picker (design VP1): every newer and every older Windows release is listed, stable and
/// beta, and nothing is chosen for the user. No I/O.
/// </summary>
public static class VersionPicker
{
    /// <summary>One release per version, newest first. A "win-v" tag wins over a legacy "v" tag of the same version.</summary>
    public static IReadOnlyList<ReleaseInfo> Distinct(IEnumerable<ReleaseInfo> releases) =>
        releases.Where(r => UpdateChecker.IsWindowsTag(r.TagName))
            .GroupBy(r => r.SemanticVersion)
            .Select(g => g.OrderByDescending(r => r.TagName.StartsWith("win-v", StringComparison.OrdinalIgnoreCase))
                          .ThenByDescending(r => r.PublishedAt).First())
            .OrderByDescending(r => r.SemanticVersion)
            .ToList();

    /// <summary>Every release newer than <paramref name="currentVersion"/>, newest first.</summary>
    public static IReadOnlyList<ReleaseInfo> Newer(IEnumerable<ReleaseInfo> releases, string currentVersion) =>
        UpdateVersion.Parse(currentVersion) is { } current
            ? Distinct(releases).Where(r => r.SemanticVersion.CompareTo(current) > 0).ToList()
            : Array.Empty<ReleaseInfo>();

    /// <summary>Every release older than <paramref name="currentVersion"/>, newest first.</summary>
    public static IReadOnlyList<ReleaseInfo> Older(IEnumerable<ReleaseInfo> releases, string currentVersion) =>
        UpdateVersion.Parse(currentVersion) is { } current
            ? Distinct(releases).Where(r => r.SemanticVersion.CompareTo(current) < 0).ToList()
            : Array.Empty<ReleaseInfo>();

    public static IReadOnlyList<ReleaseInfo> Filter(IEnumerable<ReleaseInfo> releases, VersionFilter filter) => filter switch
    {
        VersionFilter.Stable => releases.Where(r => !r.IsPreview).ToList(),
        VersionFilter.Beta => releases.Where(r => r.IsPreview).ToList(),
        _ => releases.ToList(),
    };

    public static ReleaseInfo? LatestStable(IEnumerable<ReleaseInfo> releases) =>
        releases.Where(r => !r.IsPreview).OrderByDescending(r => r.SemanticVersion).FirstOrDefault();

    public static ReleaseInfo? Newest(IEnumerable<ReleaseInfo> releases) =>
        releases.OrderByDescending(r => r.SemanticVersion).FirstOrDefault();

    /// <summary>
    /// What the start-up notice counts: newer releases on the chosen channel. Nothing while a version is pinned.
    /// </summary>
    public static IReadOnlyList<ReleaseInfo> Offered(IEnumerable<ReleaseInfo> releases, string currentVersion, bool includeBeta, string? pinnedVersion)
    {
        if (!string.IsNullOrWhiteSpace(pinnedVersion)) return Array.Empty<ReleaseInfo>();
        var newer = Newer(releases, currentVersion);
        return includeBeta ? newer : newer.Where(r => !r.IsPreview).ToList();
    }

    /// <summary>"3 newer versions found (2 stable, 1 beta)" — or "" when there are none.</summary>
    public static string OfferText(IReadOnlyCollection<ReleaseInfo> offered)
    {
        if (offered.Count == 0) return "";
        int beta = offered.Count(r => r.IsPreview), stable = offered.Count - beta;
        var noun = offered.Count == 1 ? "newer version" : "newer versions";
        var parts = new List<string>();
        if (stable > 0) parts.Add($"{stable} stable");
        if (beta > 0) parts.Add($"{beta} beta");
        return $"{offered.Count} {noun} found ({string.Join(", ", parts)})";
    }

    /// <summary>The small grey tag next to a version: "Newest", "Latest stable" or "Your previous version".</summary>
    public static string? Tag(ReleaseInfo release, IReadOnlyCollection<ReleaseInfo> sameList, string? previousVersion)
    {
        if (UpdateVersion.Parse(previousVersion) is { } previous && release.SemanticVersion.CompareTo(previous) == 0) return "Your previous version";
        var newest = Newest(sameList);
        var stable = LatestStable(sameList);
        if (newest != null && ReferenceEquals(release, newest) && release.IsPreview) return "Newest";
        if (stable != null && ReferenceEquals(release, stable)) return "Latest stable";
        if (newest != null && ReferenceEquals(release, newest)) return "Newest";
        return null;
    }

    public static bool IsRollBack(ReleaseInfo target, string currentVersion) =>
        UpdateVersion.Parse(currentVersion) is { } current && target.SemanticVersion.CompareTo(current) < 0;

    /// <summary>
    /// Moving to a beta while a newer stable than the current version exists skips that stable: the confirm step says so.
    /// </summary>
    public static ReleaseInfo? SkippedStable(ReleaseInfo target, IEnumerable<ReleaseInfo> releases, string currentVersion)
    {
        if (!target.IsPreview || IsRollBack(target, currentVersion)) return null;
        return LatestStable(Newer(releases, currentVersion));
    }

    /// <summary>
    /// Adds the versions kept on this PC that GitHub did not list (offline, or a release that was removed), so a roll
    /// back to them works without a connection. A version GitHub lists keeps GitHub's entry.
    /// </summary>
    public static IReadOnlyList<ReleaseInfo> WithKept(IEnumerable<ReleaseInfo> remote, IEnumerable<ReleaseInfo> kept)
    {
        var list = remote.ToList();
        var known = new HashSet<UpdateVersion>(list.Select(r => r.SemanticVersion));
        list.AddRange(kept.Where(k => known.Add(k.SemanticVersion)));
        return list;
    }

    /// <summary>True when Evict can install this release itself: the file for this edition and its checksum are published.</summary>
    public static bool CanInstall(ReleaseInfo release, bool installedMode) =>
        UpdateChecker.PickAsset(release, installedMode) is { } asset && UpdateChecker.PickChecksumAsset(release, asset) != null;
}

/// <summary>What the user chose on the picker's confirm step (design VP1).</summary>
public sealed record VersionChangeRequest(
    ReleaseInfo Target,
    string CurrentVersion,
    bool InstalledMode,
    bool BackUpSettings = true,
    bool KeepCurrentCopy = true,
    bool TargetOnThisPc = false,
    bool RestoreSettings = false,
    bool HasSettingsBackup = false,
    bool Pin = false)
{
    public bool IsRollBack => VersionPicker.IsRollBack(Target, CurrentVersion);
    public bool WillRestoreSettings => IsRollBack && RestoreSettings && HasSettingsBackup;
    public bool WillPin => IsRollBack && Pin;
}

/// <summary>Plain-word texts and settings changes for one version change (design VP1). No I/O.</summary>
public static class VersionChange
{
    /// <summary>The numbered "What will happen" list on the confirm step.</summary>
    public static IReadOnlyList<string> Steps(VersionChangeRequest r)
    {
        var to = r.Target.DisplayVersion;
        var steps = new List<string>();
        if (r.BackUpSettings) steps.Add($"Save your current settings (used with v{r.CurrentVersion})");
        if (r.KeepCurrentCopy) steps.Add($"Keep v{r.CurrentVersion} on this PC so you can come back to it");
        steps.Add(r.TargetOnThisPc
            ? $"Use the copy of v{to} saved on this PC (its SHA-256 is checked again)"
            : $"Download v{to} from GitHub and check its SHA-256");
        steps.Add(r.InstalledMode ? "Run its installer — Evict closes and starts again" : "Swap Evict.exe — Evict starts again");
        if (r.WillRestoreSettings) steps.Add($"Put back the settings you used with v{to}");
        if (r.WillPin) steps.Add($"Pin v{to} — no update notices until you unpin it");
        else steps.Add("Nothing installs on its own afterwards — every later change asks you first");
        return steps;
    }

    /// <summary>The amber note on the confirm step, or null.</summary>
    public static string? Warning(VersionChangeRequest r, IEnumerable<ReleaseInfo> releases)
    {
        if (r.IsRollBack)
            return $"You are going back to an older version. Features added after v{r.Target.DisplayVersion} will be missing until you update again.";
        if (!r.Target.IsPreview) return null;
        var skipped = VersionPicker.SkippedStable(r.Target, releases, r.CurrentVersion);
        return skipped != null && skipped.SemanticVersion.CompareTo(r.Target.SemanticVersion) < 0
            ? $"You are moving to a beta. This skips the latest stable (v{skipped.DisplayVersion}). Beta builds can be unstable — you can roll back from Version history at any time."
            : "You are moving to a beta. Beta builds can be unstable — you can roll back from Version history at any time.";
    }

    /// <summary>Records the change in the settings before Evict hands over to the new version.</summary>
    public static void ApplyChoices(AppSettings s, VersionChangeRequest r)
    {
        s.PreviousVersion = r.CurrentVersion;
        s.SkippedUpdateVersion = null;
        s.KeepVersionCopies = r.KeepCurrentCopy;
        if (r.WillPin)
        {
            s.PinnedVersion = r.Target.DisplayVersion;
            s.CheckForUpdates = false; // also quiets versions that do not know about pinning
        }
        else if (!r.IsRollBack && s.PinnedVersion != null)
        {
            // Choosing an update yourself ends the pin.
            s.PinnedVersion = null;
            s.CheckForUpdates = true;
        }
    }

    /// <summary>The settings <see cref="ApplyChoices"/> changes, taken before it runs.</summary>
    public sealed record Snapshot(string? PreviousVersion, string? SkippedUpdateVersion, bool KeepVersionCopies, string? PinnedVersion, bool CheckForUpdates);

    public static Snapshot Take(AppSettings s) =>
        new(s.PreviousVersion, s.SkippedUpdateVersion, s.KeepVersionCopies, s.PinnedVersion, s.CheckForUpdates);

    /// <summary>The change did not happen (cancelled, or the hand-over failed): no pin to a version that never ran.</summary>
    public static void Restore(AppSettings s, Snapshot before)
    {
        s.PreviousVersion = before.PreviousVersion;
        s.SkippedUpdateVersion = before.SkippedUpdateVersion;
        s.KeepVersionCopies = before.KeepVersionCopies;
        s.PinnedVersion = before.PinnedVersion;
        s.CheckForUpdates = before.CheckForUpdates;
    }

    /// <summary>Settings carried into the older settings file when it is put back (they describe this change).</summary>
    public static Dictionary<string, JsonNode?> CarriedSettings(AppSettings s) => new()
    {
        [nameof(AppSettings.PreviousVersion)] = s.PreviousVersion,
        [nameof(AppSettings.SkippedUpdateVersion)] = null,
        [nameof(AppSettings.PinnedVersion)] = s.PinnedVersion,
        [nameof(AppSettings.CheckForUpdates)] = s.CheckForUpdates,
        [nameof(AppSettings.KeepVersionCopies)] = s.KeepVersionCopies,
    };
}

/// <summary>
/// Copies of Evict versions kept on this PC (design VP1) — %LOCALAPPDATA%\Evict\versions\&lt;version&gt;\ holds the
/// verified installer or Evict.exe (with its SHA-256 next to it) and the settings that version used. A roll back to a
/// version found here needs no download.
/// </summary>
public sealed class VersionArchive
{
    public const string SettingsFileName = "settings.json";
    private readonly string _root;

    public VersionArchive(string root) { _root = root; }

    public static VersionArchive Default => new(Path.Combine(AppPaths.DataRoot, "versions"));

    public string Root => _root;

    private string Folder(string version)
    {
        var name = UpdateVersion.Parse(version)?.ToString() ?? version;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(_root, name);
    }

    private const string ReleaseFileName = "release.json";

    /// <summary>
    /// Copies a verified file into the version's folder and records its SHA-256 (and, when known, the release's date
    /// and notes, so the version can be listed offline). Returns the stored path.
    /// </summary>
    public string Keep(string version, string sourceFile, string fileName, string sha256, ReleaseInfo? release = null)
    {
        var folder = Folder(version);
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, Path.GetFileName(fileName));
        if (!Path.GetFullPath(sourceFile).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourceFile, target, overwrite: true);
        File.WriteAllText(target + ".sha256", sha256.ToLowerInvariant());
        if (release != null)
        {
            try
            {
                var meta = new JsonObject
                {
                    ["tag"] = release.TagName,
                    ["name"] = release.Name,
                    ["url"] = release.HtmlUrl,
                    ["notes"] = release.Body,
                    ["published"] = release.PublishedAt?.ToString("O"),
                    ["prerelease"] = release.IsPreview,
                };
                File.WriteAllText(Path.Combine(folder, ReleaseFileName), meta.ToJsonString());
            }
            catch { /* the copy is still usable without its date and notes */ }
        }
        Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow);
        return target;
    }

    /// <summary>
    /// The versions kept here as releases whose files are the kept copies (with their recorded SHA-256), for the
    /// version picker when GitHub cannot be reached. Folders without a kept program file are left out.
    /// </summary>
    public IReadOnlyList<ReleaseInfo> KeptReleases()
    {
        var list = new List<ReleaseInfo>();
        if (!Directory.Exists(_root)) return list;
        foreach (var folder in Directory.EnumerateDirectories(_root))
        {
            try
            {
                var version = UpdateVersion.Parse(Path.GetFileName(folder));
                if (version is null) continue;
                var files = Directory.EnumerateFiles(folder, "*.exe")
                    .Where(f => File.Exists(f + ".sha256"))
                    .Select(f => Path.GetFileName(f)!)
                    .ToList();
                if (files.Count == 0) continue;
                JsonObject? meta = null;
                var metaFile = Path.Combine(folder, ReleaseFileName);
                if (File.Exists(metaFile)) { try { meta = JsonNode.Parse(File.ReadAllText(metaFile)) as JsonObject; } catch { /* listed without it */ } }
                var tag = (string?)meta?["tag"] ?? "win-v" + version;
                DateTimeOffset? published = DateTimeOffset.TryParse((string?)meta?["published"], out var p) ? p : null;
                var assets = files.SelectMany(f => new[]
                {
                    new ReleaseAsset(f, new Uri(Path.Combine(folder, f)).AbsoluteUri, new FileInfo(Path.Combine(folder, f)).Length),
                    new ReleaseAsset(f + ".sha256", new Uri(Path.Combine(folder, f + ".sha256")).AbsoluteUri, 0),
                }).ToList();
                list.Add(new ReleaseInfo(version.Core, tag, (string?)meta?["name"] ?? tag, (string?)meta?["url"] ?? UpdateChecker.ReleasesUrl,
                    (string?)meta?["notes"], published, version.IsPrerelease || ((bool?)meta?["prerelease"] ?? false), assets));
            }
            catch { /* one unreadable folder does not hide the others */ }
        }
        return list;
    }

    /// <summary>True when a copy of this file is kept for the version (not yet checked against its hash).</summary>
    public bool Has(string version, string fileName)
    {
        var file = Path.Combine(Folder(version), Path.GetFileName(fileName));
        return File.Exists(file) && File.Exists(file + ".sha256");
    }

    /// <summary>The kept file, only when it still matches the SHA-256 recorded when it was kept.</summary>
    public string? Find(string version, string fileName)
    {
        var file = Path.Combine(Folder(version), Path.GetFileName(fileName));
        try
        {
            if (!File.Exists(file) || !File.Exists(file + ".sha256")) return null;
            var expected = UpdateChecker.ParseSha256(File.ReadAllText(file + ".sha256"));
            return expected != null && string.Equals(Sha256(file), expected, StringComparison.OrdinalIgnoreCase) ? file : null;
        }
        catch { return null; }
    }

    public static string Sha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>Saves the settings the given version used, so a later roll back to it can restore them.</summary>
    public bool BackUpSettings(string version, string settingsFile)
    {
        try
        {
            if (!File.Exists(settingsFile)) return false;
            var folder = Folder(version);
            Directory.CreateDirectory(folder);
            File.Copy(settingsFile, Path.Combine(folder, SettingsFileName), overwrite: true);
            return true;
        }
        catch { return false; }
    }

    public string? SettingsBackup(string version)
    {
        var file = Path.Combine(Folder(version), SettingsFileName);
        return File.Exists(file) ? file : null;
    }

    /// <summary>
    /// Replaces <paramref name="settingsFile"/> with the settings saved for <paramref name="version"/> and then applies
    /// <paramref name="overrides"/> (top-level settings such as the pin). Returns false — and leaves the file alone —
    /// when there is no backup or it cannot be read as a settings object.
    /// </summary>
    public bool RestoreSettings(string version, string settingsFile, IReadOnlyDictionary<string, JsonNode?>? overrides = null)
    {
        var backup = SettingsBackup(version);
        if (backup is null) return false;
        JsonObject json;
        try
        {
            // A damaged or non-object backup must never replace the current, valid settings.
            if (JsonNode.Parse(File.ReadAllText(backup)) is not JsonObject parsed) return false;
            json = parsed;
        }
        catch { return false; }
        if (overrides != null)
            foreach (var (key, value) in overrides) json[key] = value?.DeepClone();
        var directory = Path.GetDirectoryName(Path.GetFullPath(settingsFile))!;
        Directory.CreateDirectory(directory);
        var staged = Path.Combine(directory, "." + Path.GetFileName(settingsFile) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        File.WriteAllText(staged, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(staged, settingsFile, overwrite: true);
        return true;
    }

    /// <summary>Version folders, newest kept first.</summary>
    public IReadOnlyList<string> Versions()
    {
        if (!Directory.Exists(_root)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(_root)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .Select(d => Path.GetFileName(d)!)
            .ToList();
    }

    /// <summary>Keeps the <paramref name="keep"/> most recently used version folders plus the protected ones.</summary>
    public void Prune(int keep, IEnumerable<string?> protect)
    {
        var keepNames = new HashSet<string>(protect.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => Path.GetFileName(Folder(v!))),
            StringComparer.OrdinalIgnoreCase);
        int kept = 0;
        foreach (var name in Versions())
        {
            if (keepNames.Contains(name)) continue;
            if (kept++ < keep) continue;
            try { Directory.Delete(Path.Combine(_root, name), recursive: true); } catch { /* in use – next time */ }
        }
    }
}
