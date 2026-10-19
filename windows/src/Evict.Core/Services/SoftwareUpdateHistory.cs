using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Evict.Core.Services;

/// <summary>How one update ended. <see cref="Running"/> and <see cref="Waiting"/> only exist while a run is on screen.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SoftwareUpdateOutcome { Updated, UpdatedRestartNeeded, Failed, Cancelled, Running, Waiting }

/// <summary>One program (or Evict itself) updated, or tried to be, from the Software Updater (design SH1).</summary>
public sealed class SoftwareUpdateHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string PackageId { get; set; } = "";
    public string? Source { get; set; }
    public string? FromVersion { get; set; }
    public string? ToVersion { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public SoftwareUpdateOutcome Outcome { get; set; }
    public string Message { get; set; } = "";
    /// <summary>"You · Update 3 programs", "Auto update", …</summary>
    public string StartedBy { get; set; } = "";
    /// <summary>The last lines winget (or the self-updater) wrote for this package.</summary>
    public List<string> Output { get; set; } = new();
    /// <summary>Evict's own update (design SH1 choice E1) rather than a winget package.</summary>
    public bool IsEvict { get; set; }

    [JsonIgnore] public TimeSpan? Duration => FinishedUtc is { } f && f >= StartedUtc ? f - StartedUtc : null;
    [JsonIgnore] public bool IsSuccess => Outcome is SoftwareUpdateOutcome.Updated or SoftwareUpdateOutcome.UpdatedRestartNeeded;
    [JsonIgnore] public bool IsFinished => Outcome is not (SoftwareUpdateOutcome.Running or SoftwareUpdateOutcome.Waiting);
}

/// <summary>Evict's own update that was handed to the installer / exe swap; resolved on the next start.</summary>
public sealed class PendingSelfUpdate
{
    public string FromVersion { get; set; } = "";
    public string ToVersion { get; set; } = "";
    public string? Tag { get; set; }
    public DateTime StartedUtc { get; set; }
    public string StartedBy { get; set; } = "You";
    public List<string> Output { get; set; } = new();
}

/// <summary>Texts and rules shared by the store and the Software Updater page.</summary>
public static class SoftwareUpdateHistory
{
    /// <summary>Today plus the 6 days before it (design SH1 choice D1).</summary>
    public const int KeepDays = 7;
    public const int MaxOutputLines = 40;
    public const int MaxOutputLineLength = 300;
    public const string EvictPackageId = "Evict (this app)";

    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    /// <summary>The first local day that is still kept, for <paramref name="nowLocal"/>.</summary>
    public static DateTime OldestKeptDay(DateTime nowLocal) => nowLocal.Date.AddDays(-(KeepDays - 1));

    public static bool IsKept(SoftwareUpdateHistoryEntry entry, DateTime nowLocal) =>
        entry.StartedUtc.ToLocalTime().Date >= OldestKeptDay(nowLocal);

    /// <summary>"Today · Mon 19 Oct", "Yesterday · Sun 18 Oct", "Fri 16 Oct".</summary>
    public static string DayLabel(DateTime localDay, DateTime nowLocal)
    {
        var day = localDay.Date;
        var text = day.ToString("ddd d MMM", En);
        if (day == nowLocal.Date) return "Today · " + text;
        if (day == nowLocal.Date.AddDays(-1)) return "Yesterday · " + text;
        return text;
    }

    /// <summary>"today 16:41", "yesterday 21:03", "Fri 16 Oct".</summary>
    public static string WhenText(DateTime startedUtc, DateTime nowLocal)
    {
        var local = startedUtc.ToLocalTime();
        if (local.Date == nowLocal.Date) return "today " + local.ToString("HH:mm", En);
        if (local.Date == nowLocal.Date.AddDays(-1)) return "yesterday " + local.ToString("HH:mm", En);
        return local.ToString("ddd d MMM", En);
    }

    /// <summary>The newest finished result for a winget package (Evict's own entries never match).</summary>
    public static SoftwareUpdateHistoryEntry? LatestFor(IEnumerable<SoftwareUpdateHistoryEntry> entries, string packageId) => entries
        .Where(e => !e.IsEvict && e.IsFinished && string.Equals(e.PackageId, packageId, StringComparison.OrdinalIgnoreCase))
        .MaxBy(e => e.StartedUtc);

    /// <summary>Note on a row of the Updates tab when its last try did not work (design SH1·2); empty otherwise.</summary>
    public static string LastResultNote(SoftwareUpdateHistoryEntry? last, DateTime nowLocal) => last?.Outcome switch
    {
        SoftwareUpdateOutcome.Failed => $"Failed {WhenText(last.StartedUtc, nowLocal)} — why?",
        SoftwareUpdateOutcome.Cancelled => $"Cancelled {WhenText(last.StartedUtc, nowLocal)}",
        _ => "",
    };

    /// <summary>"6 s", "1 min 12 s", "1 h 3 min"; "—" when unknown.</summary>
    public static string DurationText(TimeSpan? duration)
    {
        if (duration is not { } d) return "—";
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalSeconds < 60) return $"{Math.Max(0, (int)Math.Round(d.TotalSeconds))} s";
        if (d.TotalMinutes < 60) return d.Seconds == 0 ? $"{(int)d.TotalMinutes} min" : $"{(int)d.TotalMinutes} min {d.Seconds} s";
        return d.Minutes == 0 ? $"{(int)d.TotalHours} h" : $"{(int)d.TotalHours} h {d.Minutes} min";
    }

    public static string OutcomeText(SoftwareUpdateOutcome outcome) => outcome switch
    {
        SoftwareUpdateOutcome.Updated => "Updated",
        SoftwareUpdateOutcome.UpdatedRestartNeeded => "Updated · restart needed",
        SoftwareUpdateOutcome.Failed => "Failed",
        SoftwareUpdateOutcome.Cancelled => "Cancelled",
        SoftwareUpdateOutcome.Running => "Updating…",
        _ => "Waiting",
    };

    /// <summary>Maps the Software Updater's per-package result to an outcome and a plain sentence.</summary>
    public static (SoftwareUpdateOutcome Outcome, string Message) Describe(string name, string? fromVersion, bool ok, string message, bool cancelled)
    {
        if (cancelled)
            return (SoftwareUpdateOutcome.Cancelled, $"You pressed Cancel updates before it finished. {name} is still on {fromVersion ?? "its old version"}.");
        if (ok && WingetService.IsRestartToFinish(message))
            return (SoftwareUpdateOutcome.UpdatedRestartNeeded, "Installed. Windows needs a restart to finish — until then the old version may still run.");
        if (ok) return (SoftwareUpdateOutcome.Updated, $"{name} is on the new version.");
        var reason = string.IsNullOrWhiteSpace(message) ? "winget reported an error" : message.Trim().TrimEnd('.');
        return (SoftwareUpdateOutcome.Failed, $"{Capitalise(reason)}. Nothing changed — {name} is still on {fromVersion ?? "its old version"}.");
    }

    /// <summary>Keeps the last <see cref="MaxOutputLines"/> non-empty lines, each shortened to <see cref="MaxOutputLineLength"/>.</summary>
    public static List<string> TrimOutput(IEnumerable<string> lines) => lines
        .Select(l => l?.Trim() ?? "")
        .Where(l => l.Length > 0)
        .Select(l => l.Length > MaxOutputLineLength ? l[..MaxOutputLineLength] + "…" : l)
        .TakeLast(MaxOutputLines)
        .ToList();

    /// <summary>"8 updated · 1 failed · 1 cancelled", or null when nothing was recorded.</summary>
    public static string? Summary(IReadOnlyCollection<SoftwareUpdateHistoryEntry> entries)
    {
        var finished = entries.Where(e => e.IsFinished).ToList();
        if (finished.Count == 0) return null;
        var parts = new List<string> { $"{finished.Count(e => e.IsSuccess)} updated" };
        int failed = finished.Count(e => e.Outcome == SoftwareUpdateOutcome.Failed);
        int cancelled = finished.Count(e => e.Outcome == SoftwareUpdateOutcome.Cancelled);
        if (failed > 0) parts.Add($"{failed} failed");
        if (cancelled > 0) parts.Add($"{cancelled} cancelled");
        return string.Join(" · ", parts);
    }

    public static string ToCsv(IEnumerable<SoftwareUpdateHistoryEntry> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Started,Finished,Program,Package,From,To,Result,Took,Source,Started by,Message");
        foreach (var e in entries.OrderByDescending(e => e.StartedUtc))
        {
            sb.AppendLine(string.Join(",",
                Csv(e.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", En)),
                Csv(e.FinishedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", En)),
                Csv(e.Name), Csv(e.PackageId), Csv(e.FromVersion), Csv(e.ToVersion),
                Csv(OutcomeText(e.Outcome)), Csv(DurationText(e.Duration)), Csv(e.Source), Csv(e.StartedBy), Csv(e.Message)));
        }
        return sb.ToString();
    }

    /// <summary>Plain-text block for "Copy details".</summary>
    public static string DetailsText(SoftwareUpdateHistoryEntry e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{e.Name} ({e.PackageId})");
        sb.AppendLine($"Result: {OutcomeText(e.Outcome)}");
        sb.AppendLine($"Version: {e.FromVersion ?? "?"} → {e.ToVersion ?? "?"}");
        sb.AppendLine($"Started: {e.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", En)}");
        sb.AppendLine($"Took: {DurationText(e.Duration)}");
        if (!string.IsNullOrEmpty(e.Source)) sb.AppendLine($"Updated from: {e.Source}");
        if (!string.IsNullOrEmpty(e.StartedBy)) sb.AppendLine($"Started by: {e.StartedBy}");
        sb.AppendLine(e.Message);
        if (e.Output.Count > 0)
        {
            sb.AppendLine();
            foreach (var line in e.Output) sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private static string Csv(string? s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>
/// Keeps the Software Updater's history for <see cref="SoftwareUpdateHistory.KeepDays"/> days in
/// %LocalAppData%\Evict\update-history.json. Older entries are dropped on every load and every write.
/// </summary>
public sealed class SoftwareUpdateHistoryStore
{
    private sealed class FileShape
    {
        public List<SoftwareUpdateHistoryEntry> Entries { get; set; } = new();
        public PendingSelfUpdate? PendingSelfUpdate { get; set; }
        /// <summary>The Evict version seen at the last start, to notice updates made outside Evict.</summary>
        public string? LastSeenEvictVersion { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly string? _file;
    private readonly Func<DateTime> _nowUtc;
    private FileShape _data = new();

    public SoftwareUpdateHistoryStore() : this(null, () => DateTime.UtcNow) { }
    internal SoftwareUpdateHistoryStore(string? file, Func<DateTime> nowUtc) { _file = file; _nowUtc = nowUtc; }
    private string FilePath => _file ?? AppPaths.SoftwareUpdateHistoryFile;
    private DateTime NowLocal => _nowUtc().ToLocalTime();

    /// <summary>Raised after any change, on the thread that made it.</summary>
    public event Action? Changed;

    /// <summary>Kept entries, newest first.</summary>
    public IReadOnlyList<SoftwareUpdateHistoryEntry> Entries
    {
        get { lock (_gate) return _data.Entries.OrderByDescending(e => e.StartedUtc).ToList(); }
    }

    public PendingSelfUpdate? Pending { get { lock (_gate) return _data.PendingSelfUpdate; } }

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                _data = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<FileShape>(File.ReadAllText(FilePath), Options) ?? new()
                    : new();
            }
            catch (Exception ex) { Log.Warn("Could not read the update history: " + ex.Message); _data = new(); }
            _data.Entries ??= new();
            // Only finished results are ever written; anything else in the file is not a result.
            _data.Entries.RemoveAll(e => e is null || !e.IsFinished);
            if (Prune()) Persist();
        }
    }

    /// <summary>Records a finished update. Unfinished outcomes are ignored.</summary>
    public void Add(SoftwareUpdateHistoryEntry entry)
    {
        if (!entry.IsFinished) return;
        entry.Output = SoftwareUpdateHistory.TrimOutput(entry.Output ?? new());
        lock (_gate)
        {
            _data.Entries.RemoveAll(e => e.Id == entry.Id);
            _data.Entries.Add(entry);
            Prune();
            Persist();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _data.Entries.Clear();
            Persist();
        }
        Changed?.Invoke();
    }

    /// <summary>Drops entries older than the window; true when something was removed.</summary>
    public bool PruneNow()
    {
        bool removed;
        lock (_gate)
        {
            removed = Prune();
            if (removed) Persist();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    // ───────────── Evict's own updates (design SH1 choice E1) ─────────────

    /// <summary>Called just before the installer starts or the exe is swapped; the next start resolves it.</summary>
    public void BeginSelfUpdate(string fromVersion, string toVersion, string? tag, string startedBy, IEnumerable<string>? output = null)
    {
        lock (_gate)
        {
            _data.PendingSelfUpdate = new PendingSelfUpdate
            {
                FromVersion = fromVersion, ToVersion = toVersion, Tag = tag, StartedUtc = _nowUtc(), StartedBy = startedBy,
                Output = SoftwareUpdateHistory.TrimOutput(output ?? Array.Empty<string>()),
            };
            Persist();
        }
    }

    /// <summary>The self-update failed before Evict closed (download, checksum or file swap).</summary>
    public void FailSelfUpdate(string fromVersion, string toVersion, string? tag, string startedBy, DateTime startedUtc, string reason, IEnumerable<string>? output = null)
    {
        lock (_gate) { _data.PendingSelfUpdate = null; }
        Add(new SoftwareUpdateHistoryEntry
        {
            Name = "Evict", PackageId = SoftwareUpdateHistory.EvictPackageId, IsEvict = true,
            Source = SelfSource(tag), FromVersion = fromVersion, ToVersion = toVersion,
            StartedUtc = startedUtc, FinishedUtc = _nowUtc(), Outcome = SoftwareUpdateOutcome.Failed, StartedBy = startedBy,
            Message = $"{reason.Trim().TrimEnd('.')}. Evict is still on {fromVersion}.",
            Output = output?.ToList() ?? new(),
        });
    }

    /// <summary>
    /// Start-up: turns a pending self-update into a result for <paramref name="currentVersion"/>, or records a version
    /// change made outside Evict (Setup run by hand, a new portable download). Returns the entry it added, if any.
    /// </summary>
    public SoftwareUpdateHistoryEntry? ResolveSelfUpdate(string currentVersion)
    {
        SoftwareUpdateHistoryEntry? entry = null;
        var current = UpdateVersion.Parse(currentVersion);
        var shown = current?.ToString() ?? currentVersion;
        lock (_gate)
        {
            var pending = _data.PendingSelfUpdate;
            var lastSeen = _data.LastSeenEvictVersion;
            if (pending != null)
            {
                bool reached = current != null && UpdateVersion.Parse(pending.ToVersion) is { } target && current.CompareTo(target) >= 0;
                entry = new SoftwareUpdateHistoryEntry
                {
                    Name = "Evict", PackageId = SoftwareUpdateHistory.EvictPackageId, IsEvict = true,
                    Source = SelfSource(pending.Tag), FromVersion = pending.FromVersion, ToVersion = pending.ToVersion,
                    StartedUtc = pending.StartedUtc, FinishedUtc = _nowUtc(), StartedBy = pending.StartedBy,
                    Outcome = reached ? SoftwareUpdateOutcome.Updated : SoftwareUpdateOutcome.Failed,
                    Message = reached
                        ? "Evict updated itself and started again."
                        : $"The update did not finish — Evict is still on {shown}. Try again from Settings › Updates.",
                    Output = pending.Output.Concat(new[] { reached ? $"Started Evict {shown}" : $"Evict {shown} started instead of {pending.ToVersion}" }).ToList(),
                };
                _data.PendingSelfUpdate = null;
            }
            else if (current != null && UpdateVersion.Parse(lastSeen) is { } previous && current.CompareTo(previous) > 0)
            {
                var now = _nowUtc();
                entry = new SoftwareUpdateHistoryEntry
                {
                    Name = "Evict", PackageId = SoftwareUpdateHistory.EvictPackageId, IsEvict = true,
                    Source = "Installed outside Evict", FromVersion = previous.ToString(), ToVersion = shown,
                    StartedUtc = now, FinishedUtc = now, StartedBy = "Setup or a new download",
                    Outcome = SoftwareUpdateOutcome.Updated, Message = $"Evict changed from {previous} to {shown} since it last ran.",
                };
            }
            _data.LastSeenEvictVersion = shown;
            if (entry != null)
            {
                entry.Output = SoftwareUpdateHistory.TrimOutput(entry.Output);
                _data.Entries.Add(entry);
            }
            Prune();
            Persist();
        }
        if (entry != null) Changed?.Invoke();
        return entry;
    }

    private static string SelfSource(string? tag) => string.IsNullOrEmpty(tag) ? "GitHub release" : $"GitHub release {tag}";

    private bool Prune()
    {
        var now = NowLocal;
        return _data.Entries.RemoveAll(e => !SoftwareUpdateHistory.IsKept(e, now)) > 0;
    }

    private void Persist()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) { Log.Warn("Could not save the update history: " + ex.Message); }
    }
}
