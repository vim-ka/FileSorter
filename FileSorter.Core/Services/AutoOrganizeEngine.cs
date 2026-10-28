using FileSorter.Abstractions;
using FileSorter.Models;

namespace FileSorter.Services;

/// <param name="Debounce">Quiet time after the last change before a run; also the retry delay when the folder is busy.</param>
/// <param name="Stability">A file must be unchanged this long; also the retry delay when files were skipped for now.</param>
/// <param name="NotificationInterval">At most one notification per folder per interval (see <see cref="NotificationThrottle"/>).</param>
public sealed record AutoOrganizeOptions(TimeSpan Debounce, TimeSpan Stability, TimeSpan NotificationInterval)
{
    public static AutoOrganizeOptions Default { get; } =
        new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
}

/// <summary>
/// Organizes the root of auto-enabled folders on new files (watcher + debounce) and/or on a
/// schedule. All public members must be called on the UI thread, and all engine state is only
/// touched there: watcher and timer callbacks are marshaled through the dispatcher, and awaits
/// resume on the UI thread's synchronization context. Heavy work (scan, lock checks, organizing)
/// runs on background threads.
/// </summary>
public sealed class AutoOrganizeEngine : IDisposable
{
    private static readonly TimeSpan MinRetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1); // ~49.7 days

    private readonly FileOrganizerService _organizer;
    private readonly CopyJournal _journal;
    private readonly FolderGate _gate;
    private readonly IUiDispatcher _dispatcher;
    private readonly AutoOrganizeOptions _options;
    private readonly Func<AutoOrganizeSettings, TimeSpan> _scheduleInterval;
    private readonly IReadOnlyList<FileCategory> _categories = FileCategoryService.GetDefaultCategories();
    private readonly CancellationTokenSource _disposeCts = new();

    /// <summary>Active (auto-enabled, not paused) folders by normalized path.</summary>
    private readonly Dictionary<string, FolderState> _folders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Roots of all auto-enabled folders, paused or not (normalized).</summary>
    private HashSet<string> _autoRoots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files the engine put into an auto-enabled root, by normalized path, until they change.</summary>
    private readonly Dictionary<string, (long Size, DateTime LastWriteTimeUtc)> _placed = new(StringComparer.OrdinalIgnoreCase);

    private bool _configured;
    private bool _disposed;

    public AutoOrganizeEngine(FileOrganizerService organizer, CopyJournal journal, FolderGate gate,
        IUiDispatcher dispatcher, AutoOrganizeOptions options, Func<AutoOrganizeSettings, TimeSpan>? scheduleInterval = null)
    {
        _organizer = organizer;
        _journal = journal;
        _gate = gate;
        _dispatcher = dispatcher;
        _options = options;
        _scheduleInterval = scheduleInterval ?? DefaultScheduleInterval;
    }

    /// <summary>Raised on the UI thread after a run that moved or copied something (folder as configured, operation).</summary>
    public event Action<string, OrganizeOperation>? RunCompleted;

    /// <summary><c>ScheduleInterval × ScheduleUnit</c>; an interval below 1 counts as 1.</summary>
    public static TimeSpan DefaultScheduleInterval(AutoOrganizeSettings settings)
    {
        var n = Math.Max(1, settings.ScheduleInterval);
        return settings.ScheduleUnit switch
        {
            ScheduleUnit.Minutes => TimeSpan.FromMinutes(n),
            ScheduleUnit.Days => TimeSpan.FromDays(n),
            _ => TimeSpan.FromHours(n)
        };
    }

    /// <summary>
    /// Applies the folder list: watchers and schedule timers for folders with
    /// <c>Auto.Enabled && !Auto.IsPaused && !pauseAll</c>, nothing for the rest. On the first call,
    /// each such folder is checked once (files that arrived while the program wasn't running).
    /// Unchanged folders keep their pending runs and schedule; a failed watcher is recreated.
    /// </summary>
    public void Configure(IReadOnlyList<FolderProfile> profiles, bool pauseAll)
    {
        if (_disposed) return;
        var isFirst = !_configured;
        _configured = true;

        var enabled = profiles.Where(p => p.Auto.Enabled && !string.IsNullOrWhiteSpace(p.FullPath)).ToList();
        _autoRoots = enabled.Select(p => PathKey.Normalize(p.FullPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _placed.Keys.Where(p => !_autoRoots.Contains(Path.GetDirectoryName(p) ?? "")).ToList())
            _placed.Remove(path);

        var active = new Dictionary<string, FolderProfile>(StringComparer.OrdinalIgnoreCase);
        if (!pauseAll)
            foreach (var p in enabled.Where(p => !p.Auto.IsPaused))
                active.TryAdd(PathKey.Normalize(p.FullPath), p);

        foreach (var key in _folders.Keys.Where(k => !active.ContainsKey(k)).ToList())
        {
            _folders[key].Stop();
            _folders.Remove(key);
        }

        foreach (var (key, profile) in active)
        {
            if (!_folders.TryGetValue(key, out var state))
                _folders[key] = state = new FolderState(key);
            state.Profile = profile;
            ConfigureWatcher(state);
            ConfigureSchedule(state);
            if (isFirst) StartRun(state);
        }
    }

    /// <summary>Something changed in the folder root: (re)starts its debounce.</summary>
    public void NotifyChanged(string folder)
    {
        if (_disposed || string.IsNullOrWhiteSpace(folder)) return;
        if (_folders.TryGetValue(PathKey.Normalize(folder), out var state))
            SetPending(state, _options.Debounce);
    }

    /// <summary>
    /// The folder's profile is being edited (possibly half-typed): its pending or scheduled run
    /// waits until the edits have been quiet for a debounce interval. Explicit runs
    /// (<see cref="RunNowAsync"/>) are not deferred.
    /// </summary>
    public void Defer(string folder)
    {
        if (_disposed || string.IsNullOrWhiteSpace(folder)) return;
        if (!_folders.TryGetValue(PathKey.Normalize(folder), out var state)) return;
        state.DeferredUntil = DateTime.UtcNow + _options.Debounce;
        if (state.PendingTimer != null)
            SetPending(state, _options.Debounce);
    }

    /// <summary>For tests: a <see cref="Defer"/> of the folder is still in effect.</summary>
    internal bool IsDeferred(string folder) =>
        _folders.TryGetValue(PathKey.Normalize(folder), out var state) && state.DeferredUntil > DateTime.UtcNow;

    /// <summary>
    /// Organizes the folder's root now. Null when the folder isn't active, is missing, is busy
    /// (then retried after the debounce) or had nothing ready.
    /// </summary>
    public async Task<OrganizeOperation?> RunNowAsync(string folder)
    {
        if (_disposed || string.IsNullOrWhiteSpace(folder)) return null;
        return _folders.TryGetValue(PathKey.Normalize(folder), out var state) ? await RunAsync(state) : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeCts.Cancel();
        foreach (var state in _folders.Values) state.Stop();
        _folders.Clear();
    }

    private void StartRun(FolderState state) => _ = RunAsync(state);

    /// <summary>One run. File-system errors are swallowed: the files stay for the next run.</summary>
    private async Task<OrganizeOperation?> RunAsync(FolderState state)
    {
        if (!IsLive(state)) return null;
        // Copy-mode candidates are filtered by the journal: never decide before it has been read
        try { await _journal.WhenLoaded; }
        catch (Exception) { /* an unreadable journal loads as empty */ }
        if (!IsLive(state)) return null;

        var root = state.Key;
        if (!Directory.Exists(root)) return null;
        EnsureWatcher(state);

        if (!_gate.TryEnter(root))
        {
            SetPending(state, _options.Debounce);
            return null;
        }

        var hasPending = false;
        OrganizeOperation? operation = null;
        var profile = state.Profile;
        try
        {
            var files = new List<FileItem>();
            await _organizer.ScanFolderAsync(root, includeSubfolders: false,
                batch => { files.AddRange(batch); return Task.CompletedTask; }, 200, _disposeCts.Token);
            if (!IsLive(state)) return null;

            var copyMode = profile.CopyMode;
            if (copyMode) _journal.Prune(root, files.Select(f => f.FullPath));
            var placed = TakePlacedSnapshot(root, files);

            var stability = _options.Stability;
            var request = OrganizeRequest.FromProfile(profile, _categories);
            var selection = await Task.Run(() =>
            {
                var candidates = AutoOrganizeCandidates.Select(files, DateTime.UtcNow, stability,
                    f => placed.Contains(f.FullPath) || (copyMode && _journal.Contains(root, f)));
                if (candidates.Ready.Count == 0) return candidates;
                // Plan first: only files that would really move or copy are opened for the lock check
                var plan = FileOrganizerService.Plan(candidates.Ready, request);
                return AutoOrganizeCandidates.DropLocked(candidates, plan, AutoOrganizeCandidates.IsLocked);
            });
            hasPending = selection.HasPending;
            if (!IsLive(state) || selection.Ready.Count == 0) return null;

            var run = RunContext.NewAuto();
            try
            {
                operation = await _organizer.OrganizeAsync(selection.Ready, request, run, ct: _disposeCts.Token);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Files may have been moved before it failed (e.g. saving history): the service records
                // whatever was done, so pick that up — it must be remembered and reported like any run.
                operation = _organizer.History.LastOrDefault(op => op.RunId == run.RunId);
                if (operation == null) return null;
            }
            RememberPlaced(operation);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // The folder vanished mid-scan or the engine is shutting down — leave the files for the next trigger
            return null;
        }
        finally
        {
            _gate.Exit(root);
            if (hasPending && IsLive(state))
                SetPending(state, _options.Stability);
        }

        // After the gate is released, so handlers see the folder as free again
        if (operation.Moves.Count > 0 && !_disposed)
            RunCompleted?.Invoke(profile.FullPath, operation);
        return operation;
    }

    /// <summary>
    /// The placed files of this root that are still unchanged; forgets the ones that changed or are gone.
    /// </summary>
    private HashSet<string> TakePlacedSnapshot(string root, List<FileItem> rootFiles)
    {
        var unchanged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_placed.Count == 0) return unchanged;

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in rootFiles)
        {
            present.Add(file.FullPath);
            if (!_placed.TryGetValue(file.FullPath, out var mark)) continue;
            if (mark.Size == file.Size && mark.LastWriteTimeUtc == file.DateModified.ToUniversalTime())
                unchanged.Add(file.FullPath);
            else
                _placed.Remove(file.FullPath);
        }

        foreach (var path in _placed.Keys.Where(p => IsInRoot(p, root) && !present.Contains(p)).ToList())
            _placed.Remove(path);

        return unchanged;
    }

    /// <summary>Remembers files the operation put directly into the root of an auto-enabled folder.</summary>
    private void RememberPlaced(OrganizeOperation operation)
    {
        foreach (var move in operation.Moves)
        {
            if (move.IsUndone || move.WasDuplicate) continue;
            var folder = Path.GetDirectoryName(move.DestinationPath);
            if (folder != null && _autoRoots.Contains(PathKey.Normalize(folder)))
                _placed[PathKey.Normalize(move.DestinationPath)] = (move.Size, move.LastWriteTimeUtc);
        }
    }

    private static bool IsInRoot(string path, string root) =>
        string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase);

    private bool IsLive(FolderState state) => !_disposed && !state.Stopped;

    // ---- timers and watchers (callbacks arrive on pool threads and are marshaled to the UI thread) ----

    /// <summary>Replaces the folder's single pending run (debounce or retry) with one after <paramref name="delay"/>.</summary>
    private void SetPending(FolderState state, TimeSpan delay)
    {
        if (!IsLive(state)) return;
        state.PendingTimer?.Dispose();
        var version = ++state.PendingVersion;
        state.PendingTimer = new Timer(_ => Post(() =>
        {
            if (!IsLive(state) || version != state.PendingVersion) return;
            state.PendingTimer?.Dispose();
            state.PendingTimer = null;
            StartRunUnlessDeferred(state);
        }), null, ClampDelay(delay, MinRetryDelay), Timeout.InfiniteTimeSpan);
    }

    /// <summary>A due debounce, retry or schedule run; postponed while the profile was edited recently (<see cref="Defer"/>).</summary>
    private void StartRunUnlessDeferred(FolderState state)
    {
        var wait = state.DeferredUntil - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
            SetPending(state, wait);
        else
            StartRun(state);
    }

    private void ConfigureSchedule(FolderState state)
    {
        var auto = state.Profile.Auto;
        if (!auto.ScheduleEnabled)
        {
            state.StopSchedule();
            return;
        }

        var interval = ClampDelay(_scheduleInterval(auto), TimeSpan.FromMilliseconds(1));
        if (state.ScheduleTimer != null && state.ScheduleInterval == interval) return; // keep its clock running

        state.StopSchedule();
        state.ScheduleInterval = interval;
        state.ScheduleTimer = new Timer(_ => Post(() =>
        {
            if (!IsLive(state)) return;
            EnsureWatcher(state);
            StartRunUnlessDeferred(state);
        }), null, interval, interval);
    }

    private void ConfigureWatcher(FolderState state)
    {
        if (!state.Profile.Auto.OnNewFiles)
        {
            state.StopWatcher();
            return;
        }
        if (state.Watcher != null && !state.WatcherFailed) return;

        state.StopWatcher();
        if (!Directory.Exists(state.Key)) return; // retried on the next Configure, run or schedule tick

        try
        {
            var watcher = new FileSystemWatcher(state.Key)
            {
                IncludeSubdirectories = false,
                // Not DirectoryName: organizing creates subfolders, which must not trigger another run
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            FileSystemEventHandler changed = (_, _) => Post(() => OnWatcherEvent(state));
            watcher.Created += changed;
            watcher.Changed += changed;
            watcher.Renamed += (_, _) => Post(() => OnWatcherEvent(state));
            // Overflow or the folder went away: events may be lost — run once (which rebuilds it)
            watcher.Error += (_, _) => Post(() =>
            {
                if (!IsLive(state) || state.Watcher != watcher) return;
                state.WatcherFailed = true;
                SetPending(state, _options.Debounce);
            });
            watcher.EnableRaisingEvents = true;
            state.Watcher = watcher;
            state.WatcherFailed = false;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            state.StopWatcher(); // retried on the next Configure, run or schedule tick
        }
    }

    /// <summary>
    /// Rebuilds a watcher that failed (overflow, folder gone) or could not be created (folder missing
    /// at startup), if the folder exists now. Cheap when the watcher is fine.
    /// </summary>
    private void EnsureWatcher(FolderState state)
    {
        if (IsLive(state) && state.Profile.Auto.OnNewFiles && (state.Watcher == null || state.WatcherFailed))
            ConfigureWatcher(state);
    }

    private void OnWatcherEvent(FolderState state)
    {
        if (IsLive(state)) SetPending(state, _options.Debounce);
    }

    /// <summary>Runs on the UI thread, unless the engine is disposed by then; never throws.</summary>
    private void Post(Action action)
    {
        if (_disposed) return;
        try
        {
            _dispatcher.InvokeAsync(() =>
            {
                if (!_disposed) action();
            }).ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch
        {
            // Dispatcher shut down — the app is exiting
        }
    }

    private static TimeSpan ClampDelay(TimeSpan delay, TimeSpan min) =>
        delay < min ? min : delay > MaxTimerDelay ? MaxTimerDelay : delay;

    private sealed class FolderState(string key)
    {
        public string Key { get; } = key;
        public FolderProfile Profile { get; set; } = null!;
        public bool Stopped { get; private set; }

        public FileSystemWatcher? Watcher;
        public bool WatcherFailed;
        public Timer? PendingTimer;
        public int PendingVersion;
        public Timer? ScheduleTimer;
        public TimeSpan ScheduleInterval;
        /// <summary>No debounce/retry/schedule run before this (UTC); see <see cref="Defer"/>.</summary>
        public DateTime DeferredUntil;

        public void StopWatcher()
        {
            if (Watcher == null) return;
            Watcher.EnableRaisingEvents = false;
            Watcher.Dispose();
            Watcher = null;
            WatcherFailed = false;
        }

        public void StopSchedule()
        {
            ScheduleTimer?.Dispose();
            ScheduleTimer = null;
        }

        public void Stop()
        {
            Stopped = true;
            StopWatcher();
            StopSchedule();
            PendingTimer?.Dispose();
            PendingTimer = null;
            PendingVersion++;
        }
    }
}
