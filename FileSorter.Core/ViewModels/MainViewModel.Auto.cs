using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.ViewModels;

/// <summary>Auto-organize: per-folder settings and pauses, global pause, notifications, autostart.</summary>
public partial class MainViewModel
{
    private static readonly string[] AutoProperties =
    [
        nameof(AutoEnabled), nameof(AutoOnNewFiles), nameof(AutoScheduleEnabled), nameof(AutoScheduleInterval),
        nameof(AutoScheduleUnit), nameof(AutoIsPaused), nameof(PauseAll), nameof(KeepRunningInBackground),
        nameof(AutoWarnings), nameof(HasAutoWarnings)
    ];

    /// <summary>For tests: the gate shared by manual runs and the engine.</summary>
    internal FolderGate Gate => _gate;

    /// <summary>For tests: the auto-organize engine (use on the UI thread only).</summary>
    internal AutoOrganizeEngine Engine => _engine;

    public bool AutoEnabled
    {
        get => _profile.Auto.Enabled;
        set
        {
            if (!SetProfileValue(_profile.Auto.Enabled, value, (p, v) => p.Auto.Enabled = v)) return;
            // Turning auto on is an explicit "go": it lifts an earlier pause (like Resume) and organizes
            // what is already there (the engine checks only at startup)
            if (value) _profile.Auto.IsPaused = false;
            OnAutoSettingsChanged(value && _selectedFolder != null ? [_selectedFolder] : []);
        }
    }

    public bool AutoOnNewFiles
    {
        get => _profile.Auto.OnNewFiles;
        set
        {
            if (SetProfileValue(_profile.Auto.OnNewFiles, value, (p, v) => p.Auto.OnNewFiles = v))
                OnAutoSettingsChanged();
        }
    }

    public bool AutoScheduleEnabled
    {
        get => _profile.Auto.ScheduleEnabled;
        set
        {
            if (SetProfileValue(_profile.Auto.ScheduleEnabled, value, (p, v) => p.Auto.ScheduleEnabled = v))
                OnAutoSettingsChanged();
        }
    }

    /// <summary>"Every N units"; at least 1.</summary>
    public int AutoScheduleInterval
    {
        get => _profile.Auto.ScheduleInterval;
        set
        {
            var clamped = Math.Max(1, value);
            if (SetProfileValue(_profile.Auto.ScheduleInterval, clamped, (p, v) => p.Auto.ScheduleInterval = v))
                OnAutoSettingsChanged();
            else if (clamped != value)
                OnPropertyChanged(); // let the UI show the clamped value
        }
    }

    public ScheduleUnit AutoScheduleUnit
    {
        get => _profile.Auto.ScheduleUnit;
        set
        {
            if (SetProfileValue(_profile.Auto.ScheduleUnit, value, (p, v) => p.Auto.ScheduleUnit = v))
                OnAutoSettingsChanged();
        }
    }

    /// <summary>The selected folder's auto-organize is paused (set by undoing one of its operations).</summary>
    public bool AutoIsPaused => _profile.Auto.Enabled && _profile.Auto.IsPaused;

    /// <summary>Global pause; lifting it keeps the per-folder pauses.</summary>
    public bool PauseAll
    {
        get => _settings.PauseAll;
        set
        {
            if (_settings.PauseAll == value) return;
            _settings.PauseAll = value;
            OnAutoSettingsChanged(value ? [] : Folders.ToList());
        }
    }

    /// <summary>
    /// The OS autostart entry is the source of truth (read at startup); settings only mirror it. A
    /// change that could not be applied leaves the actual state, so the checkbox snaps back.
    /// </summary>
    public bool StartWithWindows
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value) return;
            _autostart.SetEnabled(value);
            _settings.StartWithWindows = _autostart.IsEnabled;
            OnPropertyChanged();
            SaveSettingsInBackground();
        }
    }

    /// <summary>Closing the window keeps the program running (in the tray) while any folder has auto-organize on.</summary>
    public bool KeepRunningInBackground => Folders.Any(f => f.Profile.Auto.Enabled);

    /// <summary>The "keeps running in the tray" hint was shown once.</summary>
    public bool TrayHintShown
    {
        get => _settings.TrayHintShown;
        set
        {
            if (_settings.TrayHintShown == value) return;
            _settings.TrayHintShown = value;
            OnPropertyChanged();
            SaveSettingsInBackground();
        }
    }

    /// <summary>
    /// "Ping-pong" warnings for the selected folder: its destinations inside another auto folder or
    /// nested auto folders, and other auto folders sending files into it.
    /// </summary>
    public IReadOnlyList<string> AutoWarnings
    {
        get
        {
            if (_selectedFolder == null) return [];
            var all = Folders.Select(f => f.Profile).ToList();
            return [.. ProfileConflicts.Find(_profile, all), .. ProfileConflicts.FindIncoming(_profile, all)];
        }
    }

    public bool HasAutoWarnings => AutoWarnings.Count > 0;

    /// <summary>How long exit waits for running organize work to reach a file boundary.</summary>
    internal static readonly TimeSpan ShutdownOrganizeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Saves settings, stops auto-organize and running organize work (manual and auto): they stop
    /// between files, and what they did is recorded in history (undoable) before this returns.
    /// Called on the UI thread; waits only for background work, at most
    /// <see cref="ShutdownOrganizeTimeout"/>. Idempotent; the view model is not used afterwards.
    /// </summary>
    public void Shutdown()
    {
        if (_isShutDown) return;
        _isShutDown = true;
        _organizeCts.Cancel();
        _engine.Dispose();
        _organizer.CompleteInFlightRuns(ShutdownOrganizeTimeout);
        SaveSettings();
    }

    private void ConfigureEngine()
    {
        if (_isShutDown) return;
        _engine.Configure(Folders.Select(f => f.Profile).ToList(), _settings.PauseAll);
    }

    /// <summary>
    /// Applies a change of any folder's auto settings or pauses: reconfigures the engine, saves,
    /// refreshes the folder indicators, then runs <paramref name="runNow"/> folders that are active
    /// (what accumulated while auto was off or paused).
    /// </summary>
    private void OnAutoSettingsChanged(IReadOnlyList<FolderViewModel>? runNow = null)
    {
        if (_selectedFolder == null && runNow == null) return; // the throwaway profile is never used
        ConfigureEngine();
        SaveSettingsInBackground();
        foreach (var folder in Folders)
            folder.RefreshStatus(_settings.PauseAll);
        RaiseAutoProperties();

        foreach (var folder in runNow ?? [])
            if (IsAutoActive(folder.Profile))
                _ = RunAutoNowAsync(folder.FullPath);
    }

    private void RaiseAutoProperties()
    {
        foreach (var name in AutoProperties)
            OnPropertyChanged(name);
    }

    private void RaiseAutoWarnings()
    {
        OnPropertyChanged(nameof(AutoWarnings));
        OnPropertyChanged(nameof(HasAutoWarnings));
    }

    private bool IsAutoActive(FolderProfile profile) =>
        profile.Auto.Enabled && !profile.Auto.IsPaused && !_settings.PauseAll;

    private async Task RunAutoNowAsync(string folder)
    {
        try
        {
            await _engine.RunNowAsync(folder);
        }
        catch (Exception)
        {
            // A failing run leaves the files for the next trigger; never crash the UI
        }
    }

    private void ResumeAuto(FolderViewModel? folder)
    {
        if (folder == null || !folder.Profile.Auto.IsPaused) return;
        folder.Profile.Auto.IsPaused = false;
        OnAutoSettingsChanged([folder]);
    }

    /// <summary>
    /// Undoing an operation pauses the auto-organize of its folder, or it would redo it at once. Called
    /// while the undo holds the folder's gate, before files are restored: the engine is reconfigured
    /// (watcher, schedule and pending runs of the folder stopped) before any auto run could start.
    /// </summary>
    private void PauseAutoForUndo(string sourceFolder)
    {
        var folder = Folders.FirstOrDefault(f => PathKey.Equal(f.FullPath, sourceFolder));
        if (folder is not { Profile.Auto: { Enabled: true, IsPaused: false } }) return;
        folder.Profile.Auto.IsPaused = true;
        OnAutoSettingsChanged([]);
    }

    private bool IsFolderBusy(string folder) => !string.IsNullOrWhiteSpace(folder) && _gate.IsBusy(folder);

    /// <summary>The gate changes on any thread (auto runs); commands are re-evaluated on the UI thread.</summary>
    private void OnGateChanged()
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            OrganizeCommand.RaiseCanExecuteChanged();
            UndoCommand.RaiseCanExecuteChanged();
            UndoOperationCommand.RaiseCanExecuteChanged();
        });
    }

    /// <summary>An auto run moved files: history, notification, and a fresh scan if the folder is shown.</summary>
    private void OnAutoRunCompleted(string folderPath, OrganizeOperation operation)
    {
        if (_isShutDown) return;
        RefreshHistory();

        _notificationThrottle.Add(PathKey.Normalize(folderPath), operation, DateTime.UtcNow, ShowAutoNotification);
        if (_notificationThrottle.HasPending && !_notificationLoopRunning)
            _ = RunNotificationLoopAsync();

        // A running organize rescans every target when it finishes
        var shown = OrganizeTargets.FirstOrDefault(f => PathKey.Equal(f.FullPath, folderPath));
        if (shown != null && !IsOrganizing)
            _ = ScanFolderAsync(shown);
    }

    /// <summary>Flushes notifications collected by the throttle until none are left.</summary>
    private async Task RunNotificationLoopAsync()
    {
        _notificationLoopRunning = true;
        try
        {
            while (_notificationThrottle.HasPending && !_isShutDown)
            {
                await Task.Delay(_autoOptions.NotificationInterval); // resumes on the UI thread
                if (!_isShutDown)
                    _notificationThrottle.Tick(DateTime.UtcNow, ShowAutoNotification);
            }
        }
        finally
        {
            _notificationLoopRunning = false;
        }
    }

    /// <summary>"Downloads: 3 files organized" with Undo for these operations (several runs if throttled).</summary>
    private void ShowAutoNotification(string folderKey, IReadOnlyList<OrganizeOperation> operations)
    {
        var name = Folders.FirstOrDefault(f => PathKey.Equal(f.FullPath, folderKey))?.Name
                   ?? FolderProfile.CreateDefault(folderKey).Name;
        var ops = operations.ToList();
        _notifications.ShowAutoOrganized(NotificationTitle, AutoNotificationMessage(name, ops),
            () => _ = _dispatcher.InvokeAsync(() =>
            {
                if (_isShutDown) return;
                if (IsOrganizing)
                    PinStatus("Another operation is running — undo it from History when it finishes");
                else
                    _ = UndoOperationsAsync(() => ops.Where(_organizer.CanUndoOperation).Reverse().ToList());
            }));
    }

    /// <summary>"Downloads: 3 files organized" — files moved or copied, not duplicates (as in the history description).</summary>
    internal static string AutoNotificationMessage(string folderName, IReadOnlyList<OrganizeOperation> operations)
    {
        var count = operations.Sum(op => op.Moves.Count(m => !m.WasDuplicate));
        return $"{folderName}: {count} {(count == 1 ? "file" : "files")} organized";
    }
}
