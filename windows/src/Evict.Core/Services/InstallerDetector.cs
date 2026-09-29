using System.Collections.Concurrent;
using System.Diagnostics;
using System.Management;
using System.Text.RegularExpressions;
using Evict.Core.Util;

namespace Evict.Core.Services;

/// <summary>A process that looks like a software installer, as seen by <see cref="InstallerDetector"/>.</summary>
public sealed record DetectedInstaller(int ProcessId, int ParentProcessId, string ImagePath, string DisplayName, string Reason, DateTime DetectedAt, string? CommandLine = null)
{
    public string FileName => PathUtil.LeafName(ImagePath);
}

/// <summary>
/// Pure heuristics that decide whether a freshly started process is a software installer. Unit tested.
/// Returns a short reason ("file name", "msiexec /i …", "description") or null when it is not an installer.
/// </summary>
public static class InstallerHeuristics
{
    private static readonly Regex NameHints = new(
        @"(^|[\s_\-\.])(setup|install|installer|installation|instal)([\s_\-\.]|$)|^setup|setup$|installer$|_setup|-setup|setup_|setup-|^install|install$|websetup|onlinesetup|offlinesetup",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TempStubs = new(@"^(is-[a-z0-9]{5}\.tmp|ns[a-z0-9]{1,6}\.tmp)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TempStubFolder = new(@"\\(is-[a-z0-9]{5}|ns[a-z0-9]{1,6})\.tmp\\[^\\]+\.(tmp|exe)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ExcludedNameFragments =
    {
        "evict", "unins", "uninstall", "remove", "update.exe", "updater", "googleupdate", "edgeupdate", "onedrive", "portable", "au_.exe",
        "trustedinstaller", "tiworker", "wuauclt", "usoclient", "mousocoreworker", "sihclient", "wsappx", "dism",
        "msiexec", // handled separately with its command line
        "installagent", "installutil", "setupapi", "setuphost", "windows10upgrade", "mediacreationtool",
        "vs_installer", "vs_setup_bootstrapper", "vs_installershell",
    };

    // Programs already installed (their self-updaters/helpers) are not new installations.
    private static readonly string[] SystemPathFragments = { @"\windows\", @"\program files\", @"\program files (x86)\", @"\appdata\local\programs\", @"\microsoft\edge\", @"\google\update\" };

    /// <param name="imagePath">Full path of the executable.</param>
    /// <param name="fileDescription">FileDescription / ProductName from the version resource (may be null).</param>
    /// <param name="commandLine">Full command line (may be null, e.g. access denied).</param>
    public static string? Classify(string? imagePath, string? fileDescription, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var file = PathUtil.LeafName(imagePath);
        var lowerPath = imagePath.Replace('/', '\\').ToLowerInvariant();
        var lowerFile = file.ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(lowerFile);

        // Windows Installer: only /i, /package or a .msi argument counts; /x (uninstall), /V (service) and repair do not.
        if (lowerFile == "msiexec.exe")
        {
            if (string.IsNullOrWhiteSpace(commandLine)) return null;
            bool install = false;
            foreach (var raw in SplitArgs(commandLine).Skip(1))
            {
                var t = raw.Trim('"').ToLowerInvariant();
                if (t is "/v" or "-v" or "/embedding" or "-embedding") return null;            // the Windows Installer service instance
                if (t.StartsWith("/x") || t.StartsWith("-x") || t is "/uninstall" or "/f" or "/fa" or "/fu" or "/fv" or "/fm" or "/fo" or "/fp" or "/fs" or "/fc" or "/fd" or "/fe") return null; // uninstall / repair
                if (t.StartsWith("/i") || t.StartsWith("-i") || t is "/package" or "/a" or "/p" or "/update" || t.EndsWith(".msi") || t.EndsWith(".msp")) install = true;
            }
            return install ? "Windows Installer package" : null;
        }
        if (lowerPath.Contains(@"\~nsu")) return null; // NSIS uninstaller self-copy (%TEMP%\~nsuA.tmp\Au_.exe)

        if (ExcludedNameFragments.Any(f => lowerFile.Contains(f))) return null;
        if (SystemPathFragments.Any(f => lowerPath.Contains(f))) return null;

        // Inno Setup / NSIS temp stubs live in %TEMP%\is-XXXXX.tmp\<name>.tmp or %TEMP%\nsXXXX.tmp\Au_.exe
        if (lowerPath.Contains(@"\temp\") && (TempStubs.IsMatch(lowerFile) || TempStubFolder.IsMatch(lowerPath))) return "installer stub in Temp";

        if (NameHints.IsMatch(stem)) return "file name";

        if (!string.IsNullOrWhiteSpace(fileDescription))
        {
            var d = fileDescription.ToLowerInvariant();
            if (d.Contains("uninstall") || d.Contains("updater") || d.Contains("update")) return null;
            if (d.Contains("setup") || d.Contains("installer") || d.Contains("installation") || d.Contains("install wizard") || d.Contains("bootstrapper"))
                return "file description";
        }

        // A versioned exe started from the Downloads folder with no telling description is most often an installer.
        if (lowerPath.Contains(@"\downloads\") && lowerFile.EndsWith(".exe") && string.IsNullOrWhiteSpace(fileDescription)
            && (stem.Contains("x64") || stem.Contains("win64") || stem.Contains("win32") || stem.Contains("x86") || Regex.IsMatch(stem, @"\d+\.\d+")))
            return "downloaded program file";

        return null;
    }

    /// <summary>"Notepad++ Installer" from a description, else a cleaned file stem ("npp.8.6.Installer.x64" → "npp 8.6 x64").</summary>
    public static string FriendlyName(string imagePath, string? fileDescription, string? productName, string? commandLine = null)
    {
        if (PathUtil.LeafName(imagePath).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) && commandLine != null)
        {
            var pkg = SplitArgs(commandLine).Select(a => a.Trim('"')).FirstOrDefault(a => a.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || a.EndsWith(".msp", StringComparison.OrdinalIgnoreCase));
            if (pkg != null) return Path.GetFileNameWithoutExtension(PathUtil.LeafName(pkg));
            return "Windows Installer package";
        }
        foreach (var candidate in new[] { productName, fileDescription })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Trim().Length > 2 && !candidate.Contains("Windows", StringComparison.OrdinalIgnoreCase))
                return candidate.Trim();
        }
        var stem = Path.GetFileNameWithoutExtension(PathUtil.LeafName(imagePath));
        stem = Regex.Replace(stem, @"(?i)[\s_\-\.]*(setup|install(er)?|x64|x86|win64|win32|online|offline)[\s_\-\.]*", " ");
        stem = Regex.Replace(stem, @"[_\-\.]+", " ").Trim();
        return string.IsNullOrWhiteSpace(stem) ? PathUtil.LeafName(imagePath) : stem;
    }

    /// <summary>Splits a Windows command line into arguments (quotes respected).</summary>
    public static List<string> SplitArgs(string commandLine)
    {
        var list = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuote = false, any = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { inQuote = !inQuote; any = true; continue; }
            if (char.IsWhiteSpace(c) && !inQuote)
            {
                if (any) { list.Add(cur.ToString()); cur.Clear(); any = false; }
                continue;
            }
            cur.Append(c); any = true;
        }
        if (any) list.Add(cur.ToString());
        return list;
    }
}

/// <summary>
/// Watches for newly started processes and raises <see cref="Detected"/> when one looks like an installer.
/// Uses WMI process-creation events (no administrator rights needed for the user's own processes) and falls back to
/// polling when WMI is unavailable. Also keeps track of process trees so a recording can wait for every helper
/// process an installer spawned.
/// </summary>
public sealed class InstallerDetector : IDisposable
{
    private ManagementEventWatcher? _watcher;
    private Timer? _pollTimer;
    private HashSet<int> _knownPids = new();
    private readonly ConcurrentDictionary<string, DateTime> _recentlyReported = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ProcessTree> _trees = new();
    /// <summary>Trees of installers reported but not (yet) recorded – so their helpers are not reported again and Track() can adopt them.</summary>
    private readonly ConcurrentDictionary<int, (ProcessTree Tree, DateTime At)> _pending = new();
    /// <summary>Evict's own process tree (Install Monitor launches, uninstallers, winget…) – never reported.</summary>
    private readonly HashSet<int> _ownPids = new() { Environment.ProcessId };
    private volatile bool _paused;
    private volatile bool _running;

    public event Action<DetectedInstaller>? Detected;
    public bool IsRunning => _running;
    public bool IsPaused { get => _paused; set => _paused = value; }
    public bool UsingWmi { get; private set; }

    public void Start()
    {
        if (_running) return;
        _running = true;
        // Connecting to WMI can take a second on a cold system – never on the UI thread.
        _ = Task.Run(() =>
        {
            if (!_running) return;
            try
            {
                var w = new ManagementEventWatcher(new WqlEventQuery("__InstanceCreationEvent", TimeSpan.FromSeconds(2), "TargetInstance ISA 'Win32_Process'"));
                w.EventArrived += OnWmiProcessCreated;
                w.Start();
                if (!_running) { try { w.Stop(); w.Dispose(); } catch { /* ignore */ } return; }
                _watcher = w;
                UsingWmi = true;
                Log.Info("Installer detection started (WMI).");
            }
            catch (Exception ex)
            {
                Log.Warn("WMI process watcher unavailable, polling instead: " + ex.Message);
                UsingWmi = false;
                _knownPids = SnapshotPids();
                if (_running) _pollTimer = new Timer(_ => Poll(), null, 3000, 3000);
            }
        });
    }

    public void Stop()
    {
        _running = false;
        try { _watcher?.Stop(); _watcher?.Dispose(); } catch { /* ignore */ }
        _watcher = null;
        _pollTimer?.Dispose(); _pollTimer = null;
        Log.Info("Installer detection stopped.");
    }

    /// <summary>Processes started by Evict itself (e.g. an installer launched through Install Monitor) are not reported.</summary>
    public void IgnoreProcess(int pid) { lock (_ownPids) _ownPids.Add(pid); }

    private void OnWmiProcessCreated(object sender, EventArrivedEventArgs e)
    {
        try
        {
            if (e.NewEvent["TargetInstance"] is not ManagementBaseObject p) return;
            int pid = Convert.ToInt32(p["ProcessId"]);
            int ppid = Convert.ToInt32(p["ParentProcessId"]);
            var path = p["ExecutablePath"] as string;
            var cmd = p["CommandLine"] as string;
            var name = p["Name"] as string ?? "";

            // Grow Evict's own tree (children of children of Evict are ours too) and every tracked/pending installer tree.
            lock (_ownPids) { if (_ownPids.Contains(ppid)) _ownPids.Add(pid); }
            foreach (var tree in _trees.Values) tree.OnProcessCreated(pid, ppid);
            foreach (var pend in _pending.Values) pend.Tree.OnProcessCreated(pid, ppid);

            if (string.IsNullOrEmpty(path)) path = ProcessUtil.GetImagePath(pid) ?? name;
            Consider(pid, ppid, path, cmd);
        }
        catch (Exception ex) { Log.Warn("Process event handling failed: " + ex.Message); }
    }

    private void Poll()
    {
        if (!_running) return;
        try
        {
            var now = SnapshotPids();
            var fresh = now.Where(pid => !_knownPids.Contains(pid)).ToList();
            _knownPids = now;
            foreach (var pid in fresh)
            {
                var path = ProcessUtil.GetImagePath(pid);
                if (path is null) continue;
                Consider(pid, 0, path, null);
            }
        }
        catch (Exception ex) { Log.Warn("Process polling failed: " + ex.Message); }
    }

    private static HashSet<int> SnapshotPids()
    {
        var set = new HashSet<int>();
        foreach (var p in Process.GetProcesses()) { set.Add(p.Id); p.Dispose(); }
        return set;
    }

    private void Consider(int pid, int ppid, string path, string? commandLine)
    {
        if (_paused || !_running) return;
        lock (_ownPids) { if (_ownPids.Contains(pid) || _ownPids.Contains(ppid)) return; }
        if (_trees.Values.Any(t => t.Contains(pid) || t.Contains(ppid))) return;          // part of a recording already
        if (_pending.Values.Any(p => p.Tree.Contains(pid) || p.Tree.Contains(ppid))) return; // helper of an installer already reported

        string? description = null, product = null;
        try
        {
            if (File.Exists(path))
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                description = vi.FileDescription; product = vi.ProductName;
            }
        }
        catch { /* locked or gone */ }

        var reason = InstallerHeuristics.Classify(path, description, commandLine);
        if (reason is null) return;

        // One notification per installer file per 2 minutes (setup.exe often re-spawns itself elevated).
        var key = path.ToLowerInvariant();
        var now = DateTime.UtcNow;
        if (_recentlyReported.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(2)) return;
        _recentlyReported[key] = now;
        foreach (var stale in _recentlyReported.Where(kv => now - kv.Value > TimeSpan.FromMinutes(10)).Select(kv => kv.Key).ToList()) _recentlyReported.TryRemove(stale, out _);

        var friendly = InstallerHeuristics.FriendlyName(path, description, product, commandLine);
        var info = new DetectedInstaller(pid, ppid, path, friendly, reason, DateTime.Now, commandLine);

        // Remember the process tree from this moment on, so a later Track() (user clicks "record") knows the helpers.
        _pending[pid] = (new ProcessTree(pid), now);
        foreach (var stale in _pending.Where(kv => now - kv.Value.At > TimeSpan.FromMinutes(15)).Select(kv => kv.Key).ToList()) _pending.TryRemove(stale, out _);

        Log.Info($"Installer detected: {friendly} ({path}) pid {pid} – {reason}");
        Detected?.Invoke(info);
    }

    // ───────────────────────────── process trees ─────────────────────────────

    /// <summary>Starts tracking the tree rooted at <paramref name="rootPid"/> (children are added from WMI events).</summary>
    public ProcessTree Track(int rootPid)
    {
        var tree = _pending.TryRemove(rootPid, out var pending) ? pending.Tree : new ProcessTree(rootPid);
        _trees[rootPid] = tree;
        return tree;
    }

    public void Untrack(ProcessTree tree) => _trees.TryRemove(tree.RootPid, out _);

    public void Dispose() => Stop();
}

/// <summary>The root process of an installation plus every descendant seen since tracking began.</summary>
public sealed class ProcessTree
{
    private readonly HashSet<int> _pids;
    private readonly object _gate = new();

    public ProcessTree(int rootPid) { RootPid = rootPid; _pids = new HashSet<int> { rootPid }; }
    public int RootPid { get; }

    public bool Contains(int pid) { lock (_gate) return _pids.Contains(pid); }
    public int Count { get { lock (_gate) return _pids.Count; } }

    internal void OnProcessCreated(int pid, int parentPid)
    {
        lock (_gate) { if (_pids.Contains(parentPid)) _pids.Add(pid); }
    }

    /// <summary>True while any process of the tree is still running.</summary>
    public bool IsAlive()
    {
        int[] snapshot;
        lock (_gate) snapshot = _pids.ToArray();
        foreach (var pid in snapshot)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (!p.HasExited) return true;
            }
            catch { /* exited */ }
        }
        return false;
    }
}
