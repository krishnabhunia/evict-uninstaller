using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Evict.Core.Models;
using Evict.Core.Util;

namespace Evict.Core.Services;

/// <summary>
/// Enumerates and removes extensions of Chromium-based browsers (Chrome, Edge, Brave, Vivaldi, Opera, Chromium)
/// and Firefox by reading their profile files directly. Removal requires the browser to be closed.
/// </summary>
public sealed class BrowserExtensionService
{
    private sealed record BrowserRoot(BrowserKind Kind, string UserDataDir, string[] ProcessNames, bool ProfilesInSubfolders = true);

    private static IEnumerable<BrowserRoot> ChromiumRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return new(BrowserKind.Chrome, Path.Combine(local, "Google", "Chrome", "User Data"), new[] { "chrome" });
        yield return new(BrowserKind.Edge, Path.Combine(local, "Microsoft", "Edge", "User Data"), new[] { "msedge" });
        yield return new(BrowserKind.Brave, Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"), new[] { "brave" });
        yield return new(BrowserKind.Vivaldi, Path.Combine(local, "Vivaldi", "User Data"), new[] { "vivaldi" });
        yield return new(BrowserKind.Chromium, Path.Combine(local, "Chromium", "User Data"), new[] { "chromium", "chrome" });
        yield return new(BrowserKind.Opera, Path.Combine(roaming, "Opera Software", "Opera Stable"), new[] { "opera" }, ProfilesInSubfolders: false);
        yield return new(BrowserKind.Opera, Path.Combine(roaming, "Opera Software", "Opera GX Stable"), new[] { "opera" }, ProfilesInSubfolders: false);
    }

    private static string FirefoxRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mozilla", "Firefox");

    public static readonly IReadOnlyDictionary<BrowserKind, string[]> ProcessNames = new Dictionary<BrowserKind, string[]>
    {
        [BrowserKind.Chrome] = new[] { "chrome" },
        [BrowserKind.Edge] = new[] { "msedge" },
        [BrowserKind.Brave] = new[] { "brave" },
        [BrowserKind.Vivaldi] = new[] { "vivaldi" },
        [BrowserKind.Chromium] = new[] { "chromium", "chrome" },
        [BrowserKind.Opera] = new[] { "opera" },
        [BrowserKind.Firefox] = new[] { "firefox" },
    };

    public static bool IsBrowserRunning(BrowserKind kind)
    {
        if (!ProcessNames.TryGetValue(kind, out var names)) return false;
        try
        {
            return names.Any(n => Process.GetProcessesByName(n).Length > 0);
        }
        catch { return false; }
    }

    // ───────────────────────────── enumeration ─────────────────────────────

    public Task<List<BrowserExtensionInfo>> GetExtensionsAsync(bool includeBuiltIn, CancellationToken ct) =>
        Task.Run(() => GetExtensions(includeBuiltIn, ct), ct);

    public List<BrowserExtensionInfo> GetExtensions(bool includeBuiltIn, CancellationToken ct)
    {
        var list = new List<BrowserExtensionInfo>();
        foreach (var root in ChromiumRoots())
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root.UserDataDir)) continue;
            foreach (var (profileDir, profileName) in ChromiumProfiles(root))
            {
                try { list.AddRange(ReadChromiumProfile(root.Kind, profileDir, profileName, includeBuiltIn)); }
                catch (Exception ex) { Log.Warn($"{root.Kind} profile {profileDir}: {ex.Message}"); }
            }
        }
        try { list.AddRange(ReadFirefox(includeBuiltIn)); }
        catch (Exception ex) { Log.Warn("Firefox: " + ex.Message); }

        foreach (var e in list)
        {
            if (!string.IsNullOrEmpty(e.ExtensionPath)) e.SizeBytes = DirectorySizeCalculator.Measure(e.ExtensionPath, ct) ?? FileSize(e.ExtensionPath);
        }
        return list.OrderBy(e => e.BrowserDisplayName).ThenBy(e => e.ProfileName).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static long FileSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
    }

    private static IEnumerable<(string Dir, string Name)> ChromiumProfiles(BrowserRoot root)
    {
        if (!root.ProfilesInSubfolders)
        {
            yield return (root.UserDataDir, "Default");
            yield break;
        }
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var localState = Path.Combine(root.UserDataDir, "Local State");
            if (File.Exists(localState))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(localState));
                if (doc.RootElement.TryGetProperty("profile", out var profile) && profile.TryGetProperty("info_cache", out var cache))
                {
                    foreach (var p in cache.EnumerateObject())
                    {
                        var name = p.Value.TryGetProperty("name", out var n) ? n.GetString() : null;
                        names[p.Name] = string.IsNullOrWhiteSpace(name) ? p.Name : name!;
                    }
                }
            }
        }
        catch { /* ignore */ }

        var dirs = SafeProfileDirs(root.UserDataDir, names);
        var display = dirs.Select(d => { var leaf = Path.GetFileName(d); return (Dir: d, Leaf: leaf, Name: names.TryGetValue(leaf, out var n) ? n : leaf); }).ToList();
        foreach (var x in display)
        {
            // Two profiles may carry the same name ("Krishna") – add the folder so their lists are not merged.
            bool clash = display.Count(o => o.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase)) > 1;
            yield return (x.Dir, clash && !x.Name.Equals(x.Leaf, StringComparison.OrdinalIgnoreCase) ? $"{x.Name} ({x.Leaf})" : x.Name);
        }
    }

    private static List<string> SafeProfileDirs(string userDataDir, Dictionary<string, string> names)
    {
        try
        {
            return Directory.EnumerateDirectories(userDataDir)
                .Where(d => { var l = Path.GetFileName(d); return l.Equals("Default", StringComparison.OrdinalIgnoreCase) || l.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) || names.ContainsKey(l); })
                .ToList();
        }
        catch { return new List<string>(); }
    }

    internal static List<BrowserExtensionInfo> ReadChromiumProfile(BrowserKind kind, string profileDir, string profileName, bool includeBuiltIn)
    {
        var result = new List<BrowserExtensionInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prefFile in new[] { "Secure Preferences", "Preferences" })
        {
            var path = Path.Combine(profileDir, prefFile);
            if (!File.Exists(path)) continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(path)); } catch { continue; }
            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("extensions", out var ext) || !ext.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var entry in settings.EnumerateObject())
                {
                    var id = entry.Name;
                    if (id.Length != 32 || !seen.Add(id)) continue;
                    var info = ParseChromiumEntry(kind, profileDir, profileName, id, entry.Value);
                    if (info is null) continue;
                    if (!includeBuiltIn && info.IsComponent) { seen.Remove(id); continue; }
                    result.Add(info);
                }
            }
        }

        // Extensions present on disk but absent from prefs (rare) – list them from the folder.
        var extDir = Path.Combine(profileDir, "Extensions");
        if (Directory.Exists(extDir))
        {
            foreach (var idDir in Directory.EnumerateDirectories(extDir))
            {
                var id = Path.GetFileName(idDir);
                if (id.Length != 32 || seen.Contains(id)) continue;
                var versionDir = Directory.EnumerateDirectories(idDir).OrderByDescending(v => v).FirstOrDefault();
                if (versionDir is null) continue;
                var manifest = ReadManifest(versionDir);
                if (manifest is null) continue;
                result.Add(new BrowserExtensionInfo
                {
                    Browser = kind, ProfileName = profileName, ProfilePath = profileDir, ExtensionId = id,
                    Name = manifest.Value.Name ?? id, Version = manifest.Value.Version, Description = manifest.Value.Description,
                    Enabled = false, FromWebStore = false, ExtensionPath = idDir, Permissions = manifest.Value.Permissions,
                });
            }
        }
        return result;
    }

    internal static BrowserExtensionInfo? ParseChromiumEntry(BrowserKind kind, string profileDir, string profileName, string id, JsonElement e)
    {
        int location = e.TryGetProperty("location", out var loc) && loc.ValueKind == JsonValueKind.Number ? loc.GetInt32() : 1;
        bool isComponent = location is 5 or 10;                   // COMPONENT / EXTERNAL_COMPONENT
        bool byPolicy = location is 7 or 9;                        // EXTERNAL_POLICY(_DOWNLOAD)
        int state = e.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 1;
        bool fromStore = e.TryGetProperty("from_webstore", out var fw) && fw.ValueKind == JsonValueKind.True;
        DateTime? installTime = null;
        if (e.TryGetProperty("install_time", out var it))
        {
            var raw = it.ValueKind == JsonValueKind.String ? it.GetString() : it.ValueKind == JsonValueKind.Number ? it.GetRawText() : null;
            if (long.TryParse(raw, out var micros)) installTime = WebkitTimeToDateTime(micros);
        }

        string? relPath = e.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        string? fullPath = null;
        if (!string.IsNullOrEmpty(relPath))
            fullPath = Path.IsPathRooted(relPath) ? relPath : Path.Combine(profileDir, "Extensions", relPath);

        string? name = null, version = null, description = null, homepage = null, defaultLocale = null;
        var permissions = new List<string>();
        if (e.TryGetProperty("manifest", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            name = Str(m, "name"); version = Str(m, "version"); description = Str(m, "description"); homepage = Str(m, "homepage_url"); defaultLocale = Str(m, "default_locale");
            if (m.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Array)
                permissions.AddRange(perms.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
        }

        // Resolve "__MSG_appName__" placeholders and fill gaps from the on-disk manifest.
        var versionDir = fullPath != null && Directory.Exists(fullPath) ? fullPath : null;
        if ((name is null || name.StartsWith("__MSG_", StringComparison.Ordinal)) && versionDir != null)
        {
            var disk = ReadManifest(versionDir);
            if (disk != null)
            {
                name = disk.Value.Name ?? name;
                version ??= disk.Value.Version;
                description ??= disk.Value.Description;
                if (permissions.Count == 0) permissions = disk.Value.Permissions;
            }
        }
        if (name != null && name.StartsWith("__MSG_", StringComparison.Ordinal) && versionDir != null)
            name = ResolveMessage(versionDir, name, defaultLocale) ?? name;
        if (description != null && description.StartsWith("__MSG_", StringComparison.Ordinal) && versionDir != null)
            description = ResolveMessage(versionDir, description, defaultLocale) ?? description;

        if (string.IsNullOrWhiteSpace(name)) name = id;

        // The removable unit is the extension's id folder (holds all versions).
        string? idFolder = fullPath != null && !Path.IsPathRooted(relPath!) ? Path.Combine(profileDir, "Extensions", id) : fullPath;

        return new BrowserExtensionInfo
        {
            Browser = kind, ProfileName = profileName, ProfilePath = profileDir, ExtensionId = id,
            Name = name!, Version = version, Description = description, HomepageUrl = homepage,
            Enabled = state == 1, FromWebStore = fromStore, InstalledByPolicy = byPolicy, IsComponent = isComponent,
            InstallTime = installTime, ExtensionPath = idFolder, Permissions = permissions,
        };
    }

    private static string? Str(JsonElement e, string prop) => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Chromium timestamps: microseconds since 1601-01-01 UTC.</summary>
    internal static DateTime? WebkitTimeToDateTime(long micros)
    {
        if (micros <= 0) return null;
        try { return new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(micros * 10).ToLocalTime(); }
        catch { return null; }
    }

    internal readonly record struct ManifestInfo(string? Name, string? Version, string? Description, List<string> Permissions);

    internal static ManifestInfo? ReadManifest(string versionDir)
    {
        try
        {
            var file = Path.Combine(versionDir, "manifest.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            var name = Str(root, "name");
            var locale = Str(root, "default_locale");
            if (name != null && name.StartsWith("__MSG_", StringComparison.Ordinal)) name = ResolveMessage(versionDir, name, locale) ?? name;
            var desc = Str(root, "description");
            if (desc != null && desc.StartsWith("__MSG_", StringComparison.Ordinal)) desc = ResolveMessage(versionDir, desc, locale) ?? desc;
            var perms = new List<string>();
            if (root.TryGetProperty("permissions", out var pe) && pe.ValueKind == JsonValueKind.Array)
                perms.AddRange(pe.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            return new ManifestInfo(name, Str(root, "version"), desc, perms);
        }
        catch { return null; }
    }

    /// <summary>Resolves "__MSG_key__" using _locales/&lt;locale&gt;/messages.json (default locale, then en, then any).</summary>
    internal static string? ResolveMessage(string versionDir, string placeholder, string? defaultLocale)
    {
        var key = placeholder.Trim();
        if (!key.StartsWith("__MSG_", StringComparison.Ordinal) || !key.EndsWith("__", StringComparison.Ordinal)) return null;
        key = key[6..^2];
        var localesDir = Path.Combine(versionDir, "_locales");
        if (!Directory.Exists(localesDir)) return null;

        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(defaultLocale)) candidates.Add(defaultLocale);
        candidates.AddRange(new[] { "en", "en_US", "en_GB" });
        try { candidates.AddRange(Directory.EnumerateDirectories(localesDir).Select(Path.GetFileName)!); } catch { /* ignore */ }

        foreach (var loc in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var file = Path.Combine(localesDir, loc, "messages.json");
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!prop.Name.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                    var msg = Str(prop.Value, "message");
                    if (!string.IsNullOrWhiteSpace(msg)) return msg.Trim();
                }
            }
            catch { /* try next */ }
        }
        return null;
    }

    // ───────────────────────────── Firefox ─────────────────────────────

    private static IEnumerable<string> FirefoxProfiles()
    {
        var ini = Path.Combine(FirefoxRoot, "profiles.ini");
        string[] lines;
        try { lines = File.Exists(ini) ? File.ReadAllLines(ini) : Array.Empty<string>(); }
        catch { lines = Array.Empty<string>(); }
        if (lines.Length == 0) yield break;
        string? path = null; bool relative = true;
        foreach (var raw in lines.Append("[end]"))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                if (path != null)
                {
                    var full = relative ? Path.Combine(FirefoxRoot, path.Replace('/', '\\')) : path;
                    if (Directory.Exists(full)) yield return full;
                }
                path = null; relative = true;
                continue;
            }
            if (line.StartsWith("Path=", StringComparison.OrdinalIgnoreCase)) path = line[5..].Trim();
            else if (line.StartsWith("IsRelative=", StringComparison.OrdinalIgnoreCase)) relative = line[11..].Trim() == "1";
        }
    }

    private static List<BrowserExtensionInfo> ReadFirefox(bool includeBuiltIn)
    {
        var result = new List<BrowserExtensionInfo>();
        foreach (var profile in FirefoxProfiles())
        {
            var file = Path.Combine(profile, "extensions.json");
            if (!File.Exists(file)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (!doc.RootElement.TryGetProperty("addons", out var addons) || addons.ValueKind != JsonValueKind.Array) continue;
                foreach (var a in addons.EnumerateArray())
                {
                    var type = Str(a, "type");
                    if (type is not ("extension" or "theme")) continue;
                    var location = Str(a, "location") ?? "";
                    bool builtIn = !location.Equals("app-profile", StringComparison.OrdinalIgnoreCase);
                    if (builtIn && !includeBuiltIn) continue;
                    var id = Str(a, "id") ?? continueId();
                    string? name = null, desc = null, home = null;
                    if (a.TryGetProperty("defaultLocale", out var dl) && dl.ValueKind == JsonValueKind.Object)
                    {
                        name = Str(dl, "name"); desc = Str(dl, "description"); home = Str(dl, "homepageURL");
                    }
                    bool active = a.TryGetProperty("active", out var ac) && ac.ValueKind == JsonValueKind.True;
                    bool userDisabled = a.TryGetProperty("userDisabled", out var ud) && ud.ValueKind == JsonValueKind.True;
                    DateTime? installed = null;
                    if (a.TryGetProperty("installDate", out var idt) && idt.ValueKind == JsonValueKind.Number)
                    {
                        try { installed = DateTimeOffset.FromUnixTimeMilliseconds(idt.GetInt64()).LocalDateTime; } catch { /* ignore */ }
                    }
                    var perms = new List<string>();
                    if (a.TryGetProperty("userPermissions", out var up) && up.ValueKind == JsonValueKind.Object && up.TryGetProperty("permissions", out var pp) && pp.ValueKind == JsonValueKind.Array)
                        perms.AddRange(pp.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));

                    result.Add(new BrowserExtensionInfo
                    {
                        Browser = BrowserKind.Firefox, ProfileName = Path.GetFileName(profile), ProfilePath = profile, ExtensionId = id,
                        Name = string.IsNullOrWhiteSpace(name) ? id : name!, Version = Str(a, "version"), Description = desc, HomepageUrl = home,
                        Enabled = active && !userDisabled, FromWebStore = (Str(a, "sourceURI") ?? "").Contains("addons.mozilla.org", StringComparison.OrdinalIgnoreCase),
                        IsComponent = builtIn, InstallTime = installed, ExtensionPath = Str(a, "path"), Permissions = perms,
                    });
                }
            }
            catch (Exception ex) { Log.Warn($"Firefox profile {profile}: {ex.Message}"); }
        }
        return result;

        static string continueId() => Guid.NewGuid().ToString();
    }

    // ───────────────────────────── removal ─────────────────────────────

    public Task<(bool Ok, string Message)> RemoveAsync(BrowserExtensionInfo ext, CancellationToken ct) => Task.Run(() => Remove(ext), ct);

    public (bool Ok, string Message) Remove(BrowserExtensionInfo ext)
    {
        if (IsBrowserRunning(ext.Browser)) return (false, $"{ext.BrowserDisplayName} is running. Close it completely (check the tray) and try again.");
        if (ext.InstalledByPolicy) return (false, "This extension is enforced by policy and would be re-installed. Remove the policy first.");
        if (ext.IsComponent) return (false, "Built-in browser components cannot be removed.");
        try
        {
            return ext.Browser == BrowserKind.Firefox ? RemoveFirefox(ext) : RemoveChromium(ext);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static (bool, string) RemoveChromium(BrowserExtensionInfo ext)
    {
        var notes = new List<string>();
        // 1. Preferences files – remove the settings entry and its integrity MAC.
        foreach (var prefFile in new[] { "Preferences", "Secure Preferences" })
        {
            var path = Path.Combine(ext.ProfilePath, prefFile);
            if (!File.Exists(path)) continue;
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(path));
                bool changed = false;
                if (node?["extensions"]?["settings"] is JsonObject settings && settings.ContainsKey(ext.ExtensionId))
                {
                    settings.Remove(ext.ExtensionId); changed = true;
                }
                if (node?["protection"]?["macs"]?["extensions"]?["settings"] is JsonObject macs && macs.ContainsKey(ext.ExtensionId))
                {
                    macs.Remove(ext.ExtensionId); changed = true;
                }
                if (node?["extensions"]?["pinned_extensions"] is JsonArray pinned)
                {
                    for (int i = pinned.Count - 1; i >= 0; i--)
                        if (pinned[i]?.GetValue<string>() == ext.ExtensionId) { pinned.RemoveAt(i); changed = true; }
                }
                if (changed)
                {
                    File.Copy(path, path + ".evict-backup", overwrite: true);
                    File.WriteAllText(path, node!.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
                    notes.Add($"updated {prefFile}");
                }
            }
            catch (Exception ex) { notes.Add($"{prefFile}: {ex.Message}"); }
        }

        // 2. Extension files and per-extension storage.
        foreach (var dir in new[]
        {
            Path.Combine(ext.ProfilePath, "Extensions", ext.ExtensionId),
            Path.Combine(ext.ProfilePath, "Local Extension Settings", ext.ExtensionId),
            Path.Combine(ext.ProfilePath, "Sync Extension Settings", ext.ExtensionId),
            Path.Combine(ext.ProfilePath, "Extension Rules", ext.ExtensionId),
            Path.Combine(ext.ProfilePath, "Extension Scripts", ext.ExtensionId),
            Path.Combine(ext.ProfilePath, "Managed Extension Settings", ext.ExtensionId),
        })
        {
            if (!Directory.Exists(dir)) continue;
            try { Directory.Delete(dir, recursive: true); notes.Add("deleted " + Path.GetFileName(Path.GetDirectoryName(dir)!)); }
            catch (Exception ex) { notes.Add($"{dir}: {ex.Message}"); }
        }
        if (!string.IsNullOrEmpty(ext.ExtensionPath) && Directory.Exists(ext.ExtensionPath))
        {
            try { Directory.Delete(ext.ExtensionPath, true); } catch { /* unpacked extension elsewhere – leave it */ }
        }
        Log.Info($"Removed extension {ext.Name} ({ext.ExtensionId}) from {ext.BrowserDisplayName}/{ext.ProfileName}: {string.Join(", ", notes)}");
        return (true, "Removed. The browser will drop the extension on next start.");
    }

    private static (bool, string) RemoveFirefox(BrowserExtensionInfo ext)
    {
        var file = Path.Combine(ext.ProfilePath, "extensions.json");
        if (File.Exists(file))
        {
            var node = JsonNode.Parse(File.ReadAllText(file));
            if (node?["addons"] is JsonArray addons)
            {
                for (int i = addons.Count - 1; i >= 0; i--)
                {
                    if (addons[i]?["id"]?.GetValue<string>() == ext.ExtensionId) addons.RemoveAt(i);
                }
                File.Copy(file, file + ".evict-backup", overwrite: true);
                File.WriteAllText(file, node!.ToJsonString());
            }
        }
        var xpi = ext.ExtensionPath ?? Path.Combine(ext.ProfilePath, "extensions", ext.ExtensionId + ".xpi");
        try
        {
            if (File.Exists(xpi)) File.Delete(xpi);
            else if (Directory.Exists(xpi)) Directory.Delete(xpi, true);
        }
        catch (Exception ex) { return (true, "Entry removed, but the add-on file could not be deleted: " + ex.Message); }
        // Firefox rebuilds addonStartup.json.lz4 from extensions.json on launch.
        var startup = Path.Combine(ext.ProfilePath, "addonStartup.json.lz4");
        try { if (File.Exists(startup)) File.Delete(startup); } catch { /* ignore */ }
        return (true, "Removed. Firefox will refresh its add-on list on next start.");
    }
}
