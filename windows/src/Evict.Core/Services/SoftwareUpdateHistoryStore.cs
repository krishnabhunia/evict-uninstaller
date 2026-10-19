using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Evict.Core.Models;

namespace Evict.Core.Services;

/// <summary>Thread-safe, atomically saved update journal. Unreadable files are never overwritten.</summary>
public sealed class SoftwareUpdateHistoryStore
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    public const int MaximumOutputLines = 80;
    public const int MaximumLineLength = 512;
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Action<string, string> _write;
    private readonly List<SoftwareUpdateAttempt> _records = new();
    private bool _loaded, _dirty;
    private string? _loadError, _saveError;

    private sealed class Journal
    {
        public Journal() { }
        [JsonRequired] public int SchemaVersion { get; set; } = 1;
        [JsonRequired] public List<SoftwareUpdateAttempt> Attempts { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SoftwareUpdateHistoryStore(string? path = null, Func<DateTimeOffset>? utcNow = null)
        : this(path ?? Path.Combine(AppPaths.DataRoot, "software-update-history.json"),
            utcNow ?? (() => DateTimeOffset.UtcNow), WriteAtomic) { }

    internal SoftwareUpdateHistoryStore(string path, Func<DateTimeOffset> utcNow, Action<string, string> write)
    { _path = path; _utcNow = utcNow; _write = write; }

    public event Action? Changed;
    public string? LoadError { get { lock (_gate) return _loadError; } }
    public string? SaveError { get { lock (_gate) return _saveError; } }
    public bool HasUnsavedChanges { get { lock (_gate) return _dirty; } }
    public DateTimeOffset CutoffUtc => _utcNow().ToUniversalTime() - Retention;

    public void Load()
    {
        lock (_gate)
        {
            if (_loaded) return;
            LoadCore();
        }
        NotifyChanged();
    }

    private void LoadCore()
    {
        _loaded = true;
        try
        {
            var existing = ReadJournal();
            var currentIds = _records.Select(r => r.Id).ToHashSet();
            foreach (var record in existing.Where(r => !currentIds.Contains(r.Id)))
                _records.Add(record.Outcome == SoftwareUpdateOutcome.InProgress
                    ? record with
                    {
                        Outcome = SoftwareUpdateOutcome.Interrupted,
                        FinishedAtUtc = _utcNow().ToUniversalTime(),
                        Message = "Evict closed before an installer result was recorded. The outcome is unknown; check the installed version before trying again.",
                    } : record);
            _loadError = null;
            _dirty |= existing.Any(r => r.Outcome == SoftwareUpdateOutcome.InProgress);
            _dirty |= PruneCore();
            if (_dirty) SaveCore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _loadError = "History could not be loaded. Existing records have not been replaced. " + ex.Message;
        }
    }

    private List<SoftwareUpdateAttempt> ReadJournal()
    {
        if (!File.Exists(_path))
        {
            // File.Exists hides access errors; do not interpret those errors as an empty history.
            try { using var stream = File.OpenRead(_path); }
            catch (FileNotFoundException) { return new(); }
            catch (DirectoryNotFoundException) { return new(); }
        }
        var journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(_path), Options)
            ?? throw new InvalidDataException("The history document is empty.");
        if (journal.SchemaVersion != 1 || journal.Attempts is null)
            throw new InvalidDataException("The history document has an unsupported format.");
        var ids = new HashSet<Guid>();
        foreach (var r in journal.Attempts)
            if (r is null || r.Id == Guid.Empty || !ids.Add(r.Id)
                || string.IsNullOrWhiteSpace(r.PackageId) || string.IsNullOrWhiteSpace(r.Name)
                || r.StartedAtUtc == default || !Enum.IsDefined(r.Outcome)
                || (r.Outcome != SoftwareUpdateOutcome.InProgress && r.FinishedAtUtc is null)
                || r.Message is null || r.Output is null || r.Output.Any(line => line is null))
                throw new InvalidDataException("The history document contains an invalid attempt.");
        return journal.Attempts.Select(r => r with
        {
            Output = r.Output.TakeLast(MaximumOutputLines).Select(TrimLine).ToArray(),
        }).ToList();
    }

    public IReadOnlyList<SoftwareUpdateAttempt> Snapshot()
    {
        Load();
        lock (_gate)
        {
            var cutoff = CutoffUtc;
            return _records.Where(r => r.Outcome == SoftwareUpdateOutcome.InProgress || r.FinishedAtUtc > cutoff)
                .OrderByDescending(r => r.FinishedAtUtc ?? r.StartedAtUtc)
                .Select(r => r with { Output = r.Output.ToArray() }).ToArray();
        }
    }

    public Guid Begin(UpgradablePackage package)
    {
        Load();
        var record = new SoftwareUpdateAttempt
        {
            Name = package.Name, PackageId = package.Id, Source = package.Source,
            PreviousVersion = package.InstalledVersion, TargetVersion = package.AvailableVersion,
            StartedAtUtc = _utcNow().ToUniversalTime(), Message = "Queued; waiting for an available update slot.",
        };
        lock (_gate) { _records.Add(record); _dirty = true; SaveCore(); }
        NotifyChanged();
        return record.Id;
    }

    public void MarkRunning(Guid id)
    {
        lock (_gate)
        {
            int index = _records.FindIndex(r => r.Id == id && r.Outcome == SoftwareUpdateOutcome.InProgress);
            if (index < 0) return;
            _records[index] = _records[index] with { Message = "Installer running; waiting for its result." };
        }
        NotifyChanged();
    }

    public void AppendOutput(Guid id, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            int index = _records.FindIndex(r => r.Id == id && r.Outcome == SoftwareUpdateOutcome.InProgress);
            if (index < 0) return;
            _records[index] = _records[index] with
            { Output = _records[index].Output.Append(TrimLine(line)).TakeLast(MaximumOutputLines).ToArray() };
            _dirty = true;
        }
    }

    public void Complete(Guid id, SoftwareUpgradeResult result)
    {
        if (result.Outcome == SoftwareUpdateOutcome.InProgress || !Enum.IsDefined(result.Outcome))
            throw new ArgumentException("A known final result is required.", nameof(result));
        lock (_gate)
        {
            int index = _records.FindIndex(r => r.Id == id && r.Outcome == SoftwareUpdateOutcome.InProgress);
            if (index < 0) return;
            _records[index] = _records[index] with
            {
                Outcome = result.Outcome, FinishedAtUtc = _utcNow().ToUniversalTime(),
                ExitCode = result.ExitCode, Message = result.Message,
            };
            _dirty = true;
            SaveCore();
        }
        NotifyChanged();
    }

    public void PruneExpired()
    {
        Load();
        bool changed;
        lock (_gate)
        {
            changed = PruneCore();
            if (changed) { _dirty = true; SaveCore(); }
        }
        if (changed) NotifyChanged();
    }

    private bool PruneCore() =>
        _records.RemoveAll(r => r.Outcome != SoftwareUpdateOutcome.InProgress && r.FinishedAtUtc <= CutoffUtc) > 0;

    public bool RetrySave()
    {
        Load();
        bool saved;
        lock (_gate)
        {
            if (_loadError != null) LoadCore();
            saved = _loadError is null && SaveCore();
        }
        NotifyChanged();
        return saved;
    }

    public bool ClearCompleted()
    {
        Load();
        bool saved;
        lock (_gate)
        {
            if (_loadError != null) return false;
            var previous = _records.ToArray();
            _records.RemoveAll(r => r.Outcome != SoftwareUpdateOutcome.InProgress);
            _dirty = true;
            saved = SaveCore();
            if (!saved) { _records.Clear(); _records.AddRange(previous); }
        }
        NotifyChanged();
        return saved;
    }

    private bool SaveCore()
    {
        if (_loadError != null) return false;
        try
        {
            PruneCore();
            _write(_path, JsonSerializer.Serialize(new Journal { Attempts = _records.ToList() }, Options));
            _saveError = null;
            _dirty = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _saveError = "History could not be saved. Keep Evict open and retry saving. " + ex.Message;
            return false;
        }
    }

    private static string TrimLine(string line)
    {
        var clean = line.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length > MaximumLineLength ? clean[..(MaximumLineLength - 1)] + "…" : clean;
    }

    private static void WriteAtomic(string path, string json)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true))
                { writer.Write(json); writer.Flush(); }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private void NotifyChanged()
    {
        foreach (var observer in Changed?.GetInvocationList() ?? Array.Empty<Delegate>())
            try { ((Action)observer)(); } catch { /* observers cannot change an installer result */ }
    }
}
