using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using FileSorter.Abstractions;
using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private const int ThumbnailSize = 48;

    private const string NotificationTitle = "FileSorter";

    private readonly FileOrganizerService _organizer = new(new CopyJournal());
    private readonly IUiDispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly IThumbnailProvider _thumbnails;
    private readonly IThemeService _theme;
    private readonly INotificationService _notifications;
    private readonly IAutostartService _autostart;

    /// <summary>One organize or undo per folder at a time — shared by manual runs and the auto engine.</summary>
    private readonly FolderGate _gate = new();
    private readonly AutoOrganizeOptions _autoOptions = AutoOrganizeOptions.Default;
    private readonly AutoOrganizeEngine _engine;
    private readonly NotificationThrottle _notificationThrottle;
    private bool _notificationLoopRunning;
    private bool _isShutDown;
    /// <summary>Cancelled on exit: manual organize runs stop between files.</summary>
    private readonly CancellationTokenSource _organizeCts = new();
    private bool _isScanning;

    /// <summary>Scanned files of each organize target, keyed by <see cref="PathKey.Normalize"/>.</summary>
    private readonly Dictionary<string, List<FileItem>> _scans = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Cancellation of each folder's running scan; a folder is scanning while it has an entry.</summary>
    private readonly Dictionary<string, CancellationTokenSource> _scanCts = new(StringComparer.OrdinalIgnoreCase);

    private AppSettings _settings = new();
    private FolderViewModel? _selectedFolder;
    /// <summary>Profile of the selected folder — the single store of per-folder options.
    /// Without a selected folder it is a throwaway default that is never saved.</summary>
    private FolderProfile _profile = FolderProfile.CreateDefault("");
    private bool _applyingProfile;
    private bool _isOrganizing;
    private int _progress;
    private string _statusText = "Select a folder to start";
    private int _totalFiles;
    private bool _isDarkTheme = true;
    private bool _showHistory;
    private FileViewMode _selectedViewMode = FileViewMode.Tree;
    private bool _throttlePending;

    // Colors for categories
    private static readonly RgbColor[] CategoryColors =
    [
        new RgbColor(100, 149, 237),  // Application - cornflower blue
        new RgbColor(186, 130, 230),  // Archive - purple
        new RgbColor(240, 98, 98),    // Documents - red/pink
        new RgbColor(245, 158, 115),  // Movies - salmon
        new RgbColor(255, 183, 77),   // Music - orange
        new RgbColor(220, 220, 90),   // Photos - yellow
        new RgbColor(102, 187, 106),  // Text/Code - green
        new RgbColor(120, 180, 220),  // Executables - light blue
        new RgbColor(158, 158, 158),  // Fonts - gray
        new RgbColor(130, 130, 150),  // Other - dim
    ];

    /// <summary>The managed folder list; nothing is evicted automatically.</summary>
    public ObservableCollection<FolderViewModel> Folders { get; } = [];

    public FolderViewModel? SelectedFolder
    {
        get => _selectedFolder;
        set => SelectFolder(value);
    }

    public string SourceFolder => _selectedFolder?.FullPath ?? "";

    public bool HasSourceFolder => _selectedFolder != null;

    public int CurrentFolderFileCount => _selectedFolder?.FileCount ?? 0;

    /// <summary>
    /// Folders the Organize button processes and the preview shows: the checked folders in list
    /// order if any are checked (the selected folder only if it is checked too), else the selected folder.
    /// </summary>
    public IReadOnlyList<FolderViewModel> OrganizeTargets
    {
        get
        {
            var checkedFolders = Folders.Where(f => f.IsChecked).ToList();
            if (checkedFolders.Count > 0) return checkedFolders;
            return _selectedFolder != null ? [_selectedFolder] : [];
        }
    }

    public string OrganizeButtonText =>
        OrganizeTargets.Count is var n && n > 1 ? $"ORGANIZE {n} FOLDERS" : "ORGANIZE";

    /// <summary>Writes a per-folder option into the selected profile; true if it changed.</summary>
    private bool SetProfileValue<T>(T current, T value, Action<FolderProfile, T> assign,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        assign(_profile, value);
        OnPropertyChanged(propertyName);
        OnProfileEdited();
        return true;
    }

    /// <summary>Quiet time after the last profile edit before it is saved.</summary>
    internal static TimeSpan ProfileSaveDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    private int _profileSaveVersion;

    /// <summary>
    /// Any edit of the selected folder's profile (text fields update per keystroke): a pending or
    /// scheduled auto run of the folder waits until the edits are quiet, so it never uses a
    /// half-typed value, and the profile is saved once the edits settle — auto runs use it, so it
    /// must survive a crash.
    /// </summary>
    private void OnProfileEdited()
    {
        if (_selectedFolder == null || _isShutDown) return; // the throwaway profile is never used
        _engine.Defer(_profile.FullPath);
        _ = SaveProfileAfterQuietAsync(++_profileSaveVersion);
    }

    private async Task SaveProfileAfterQuietAsync(int version)
    {
        await Task.Delay(ProfileSaveDelay); // resumes on the UI thread
        if (version == _profileSaveVersion)
            SaveSettingsInBackground();
    }

    public string? DestinationFolder
    {
        get => _profile.DestinationFolder;
        set
        {
            if (SetProfileValue(_profile.DestinationFolder, value, (p, v) => p.DestinationFolder = v))
            {
                OnPropertyChanged(nameof(DestinationDisplay));
                RaiseAutoWarnings();
            }
        }
    }

    public string DestinationDisplay =>
        string.IsNullOrEmpty(DestinationFolder) ? "Folder" : Path.GetFileName(DestinationFolder) ?? "Folder";

    public string DestinationSubfolderPattern
    {
        get => _profile.Template;
        set
        {
            if (SetProfileValue(_profile.Template, value ?? "", (p, v) => p.Template = v))
                RefreshPreviewDeferred();
        }
    }

    public OrganizeMethod SelectedMethod
    {
        get => _profile.Method;
        set
        {
            if (SetProfileValue(_profile.Method, value, (p, v) => p.Method = v))
            {
                OnPropertyChanged(nameof(ShowDatePanel));
                RefreshPreviewDeferred();
            }
        }
    }

    public bool IncludeSubfolders
    {
        get => _profile.IncludeSubfolders;
        set
        {
            if (SetProfileValue(_profile.IncludeSubfolders, value, (p, v) => p.IncludeSubfolders = v)
                && _selectedFolder != null && OrganizeTargets.Contains(_selectedFolder))
            {
                _pinnedStatus = null;
                _ = ScanFolderAsync(_selectedFolder);
            }
        }
    }

    public bool CopyMode
    {
        get => _profile.CopyMode;
        set
        {
            if (SetProfileValue(_profile.CopyMode, value, (p, v) => p.CopyMode = v))
                OnPropertyChanged(nameof(MoveMode));
        }
    }

    public bool MoveMode
    {
        get => !CopyMode;
        set => CopyMode = !value;
    }

    public bool ApplyFilters
    {
        get => _profile.ApplyFilters;
        set
        {
            if (SetProfileValue(_profile.ApplyFilters, value, (p, v) => p.ApplyFilters = v))
                RefreshPreviewDeferred();
        }
    }

    public bool SkipDuplicates
    {
        get => _profile.SkipDuplicates;
        set => SetProfileValue(_profile.SkipDuplicates, value, (p, v) => p.SkipDuplicates = v);
    }

    /// <summary>What happens to files no routing rule matched.</summary>
    public UnmatchedFilesMode Unmatched
    {
        get => _profile.Unmatched;
        set
        {
            if (SetProfileValue(_profile.Unmatched, value, (p, v) => p.Unmatched = v))
                RefreshPreviewDeferred();
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        set => SetProperty(ref _isScanning, value);
    }

    public bool IsOrganizing
    {
        get => _isOrganizing;
        set => SetProperty(ref _isOrganizing, value);
    }

    public int Progress
    {
        get => _progress;
        set => SetProperty(ref _progress, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public int TotalFiles
    {
        get => _totalFiles;
        set => SetProperty(ref _totalFiles, value);
    }

    /// <summary>"RULES ON" while at least one rule is effective (enabled, complete, with a destination).</summary>
    public string RulesActiveText => _profile.Rules.Any(r => r.Snapshot() != null) ? "RULES ON" : "";

    /// <summary>Groups in the last preview; the chart shows at most <see cref="PreviewBuilder.MaxStatsGroups"/>.</summary>
    private int _groupCount;

    public string TypeCountText => SelectedMethod switch
    {
        OrganizeMethod.ByType => $"{CategoryStatsItems.Count} Types",
        OrganizeMethod.ByExtension => $"{_groupCount} Extensions",
        OrganizeMethod.ByDate => $"{_groupCount} Dates",
        OrganizeMethod.ByAlphabet => $"{_groupCount} Letters",
        OrganizeMethod.BySize => $"{_groupCount} Sizes",
        OrganizeMethod.ByDateTaken => $"{_groupCount} Dates",
        OrganizeMethod.ByName => $"{_groupCount} Names",
        _ => $"{_groupCount} Groups"
    };

    public bool CanUndo => _organizer.LastUndoableRun.Count > 0;

    /// <summary>Raised after an undo with the operations that were actually undone.</summary>
    public event Action<IReadOnlyList<OrganizeOperation>>? OperationsUndone;

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set => SetProperty(ref _isDarkTheme, value);
    }

    public bool ShowHistory
    {
        get => _showHistory;
        set => SetProperty(ref _showHistory, value);
    }

    public DateGranularity SelectedDateGranularity
    {
        get => _profile.DateGranularity;
        set
        {
            if (SetProfileValue(_profile.DateGranularity, value, (p, v) => p.DateGranularity = v))
                RefreshPreviewDeferred();
        }
    }

    public bool ShowDatePanel => SelectedMethod is OrganizeMethod.ByDate or OrganizeMethod.ByDateTaken;

    public FileViewMode SelectedViewMode
    {
        get => _selectedViewMode;
        set
        {
            if (SetProperty(ref _selectedViewMode, value))
            {
                OnPropertyChanged(nameof(IsTreeView));
                OnPropertyChanged(nameof(IsIconsView));
                OnPropertyChanged(nameof(IsListView));
                OnPropertyChanged(nameof(IsDetailsView));
                OnPropertyChanged(nameof(IsNonTreeView));
            }
        }
    }

    public bool IsTreeView => SelectedViewMode == FileViewMode.Tree;
    public bool IsIconsView => SelectedViewMode == FileViewMode.Icons;
    public bool IsListView => SelectedViewMode == FileViewMode.List;
    public bool IsDetailsView => SelectedViewMode == FileViewMode.Details;
    public bool IsNonTreeView => SelectedViewMode != FileViewMode.Tree;

    public ObservableCollection<CategoryViewModel> Categories { get; } = [];
    public ObservableCollection<PreviewItem> PreviewItems { get; } = [];
    public ObservableCollection<CategoryStats> CategoryStatsItems { get; } = [];
    public ObservableCollection<ViewFileGroup> ViewFileGroups { get; } = [];
    public IReadOnlyList<ViewFileItem> FlatViewItems { get; private set; } = [];
    /// <summary>Routing rules of the selected folder, in evaluation order; rebuilt on folder switch.</summary>
    public ObservableCollection<RoutingRuleViewModel> Rules { get; } = [];
    public ObservableCollection<OrganizeOperation> HistoryItems { get; } = [];

    public RelayCommand AddFolderCommand { get; }
    public ICommand SelectDestinationCommand { get; }
    public AsyncRelayCommand OrganizeCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand RemoveRuleCommand { get; }
    public ICommand MoveRuleUpCommand { get; }
    public ICommand MoveRuleDownCommand { get; }
    public ICommand BrowseRuleDestinationCommand { get; }
    public ICommand SetMethodCommand { get; }
    public RelayCommand SelectFolderCommand { get; }
    public RelayCommand RemoveFolderCommand { get; }
    public ICommand ToggleCategoryCommand { get; }
    public ICommand SetDateGranularityCommand { get; }
    public ICommand CloseDatePanelCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ToggleHistoryCommand { get; }
    public AsyncRelayCommand ClearHistoryCommand { get; }
    public ICommand SetViewModeCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand ExpandAllPreviewCommand { get; }
    public ICommand CollapseAllPreviewCommand { get; }
    public ICommand ExpandAllViewCommand { get; }
    public ICommand CollapseAllViewCommand { get; }
    public ICommand ToggleViewGroupCommand { get; }
    public AsyncRelayCommand UndoOperationCommand { get; }
    public ICommand ResumeAutoCommand { get; }
    public ICommand TogglePauseAllCommand { get; }


    public MainViewModel(IUiDispatcher dispatcher, IDialogService dialogs,
        IThumbnailProvider thumbnails, IThemeService theme,
        INotificationService notifications, IAutostartService autostart)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _thumbnails = thumbnails;
        _theme = theme;
        _notifications = notifications;
        _autostart = autostart;
        _notificationThrottle = new NotificationThrottle(_autoOptions.NotificationInterval);
        _engine = new AutoOrganizeEngine(_organizer, _organizer.Journal, _gate, dispatcher, _autoOptions);
        _engine.RunCompleted += OnAutoRunCompleted;
        _gate.Changed += _ => OnGateChanged();

        AddFolderCommand = new RelayCommand(_ => AddFolder(), _ => !IsOrganizing);
        SelectDestinationCommand = new RelayCommand(_ => SelectDestinationFolder());
        OrganizeCommand = new AsyncRelayCommand(_ => OrganizeAsync(), _ => CanOrganize());
        UndoCommand = new AsyncRelayCommand(_ => UndoLastAsync(),
            _ => !IsOrganizing && CanUndo && !_organizer.LastUndoableRun.Any(op => IsFolderBusy(op.SourceFolder)));
        UndoOperationCommand = new AsyncRelayCommand(
            p => UndoOperationAsync(p as OrganizeOperation),
            p => !IsOrganizing && p is OrganizeOperation op && _organizer.CanUndoOperation(op)
                 && !IsFolderBusy(op.SourceFolder));
        ResumeAutoCommand = new RelayCommand(p => ResumeAuto(p as FolderViewModel ?? _selectedFolder));
        TogglePauseAllCommand = new RelayCommand(_ => PauseAll = !PauseAll);
        AddRuleCommand = new RelayCommand(_ => AddRule());
        RemoveRuleCommand = new RelayCommand(p =>
        {
            if (p is RoutingRuleViewModel r) RemoveRule(r);
        });
        MoveRuleUpCommand = new RelayCommand(p =>
        {
            if (p is RoutingRuleViewModel r) MoveRule(r, -1);
        });
        MoveRuleDownCommand = new RelayCommand(p =>
        {
            if (p is RoutingRuleViewModel r) MoveRule(r, +1);
        });
        BrowseRuleDestinationCommand = new RelayCommand(p =>
        {
            if (p is RoutingRuleViewModel r && _dialogs.PickFolder("Select Rule Destination") is { } path)
                r.Destination = path;
        });
        SetMethodCommand = new RelayCommand(p =>
        {
            if (p is OrganizeMethod m)
                SelectedMethod = m;
        });
        SelectFolderCommand = new RelayCommand(p =>
        {
            if (p is FolderViewModel f)
                SelectFolder(f);
        }, _ => !IsOrganizing);
        RemoveFolderCommand = new RelayCommand(p =>
        {
            if (p is FolderViewModel f)
                RemoveFolder(f);
        }, _ => !IsOrganizing);
        SetDateGranularityCommand = new RelayCommand(p =>
        {
            if (p is DateGranularity g)
                SelectedDateGranularity = g;
        });
        CloseDatePanelCommand = new RelayCommand(_ =>
        {
            SelectedMethod = OrganizeMethod.ByType;
        });
        ToggleCategoryCommand = new RelayCommand(p =>
        {
            if (p is string name)
            {
                var cat = Categories.FirstOrDefault(c => c.Name == name);
                if (cat != null)
                    cat.IsSelected = !cat.IsSelected;
            }
        });
        ToggleThemeCommand = new RelayCommand(_ => ToggleTheme());
        ToggleHistoryCommand = new RelayCommand(_ => ShowHistory = !ShowHistory);
        ClearHistoryCommand = new AsyncRelayCommand(async _ =>
        {
            await _organizer.ClearHistoryAsync();
            RefreshHistory();
        }, _ => !IsOrganizing);
        SetViewModeCommand = new RelayCommand(p =>
        {
            if (p is FileViewMode mode)
                SelectedViewMode = mode;
        });
        OpenFileCommand = new RelayCommand(p =>
        {
            if (p is string path && System.IO.File.Exists(path))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true
                    });
                }
                catch { }
            }
        });
        ToggleViewGroupCommand = new RelayCommand(p =>
        {
            if (p is ViewFileGroup g)
            {
                g.IsExpanded = !g.IsExpanded;
                RebuildFlatViewItems();
            }
        });
        ExpandAllPreviewCommand = new RelayCommand(_ => SetPreviewExpanded(true));
        CollapseAllPreviewCommand = new RelayCommand(_ => SetPreviewExpanded(false));
        ExpandAllViewCommand = new RelayCommand(_ => { SetViewGroupsExpanded(true); RebuildFlatViewItems(); });
        CollapseAllViewCommand = new RelayCommand(_ => { SetViewGroupsExpanded(false); RebuildFlatViewItems(); });

        LoadCategories();
        LoadSettings();
        PropertyChanged += OnOwnPropertyChanged;
        _ = LoadHistoryAsync();
        // The first configuration also checks each active folder once (what arrived while not running)
        ConfigureEngine();
    }

    // Commands don't get automatic requery outside WPF, so raise CanExecuteChanged
    // whenever state they depend on changes.
    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IsOrganizing) or nameof(IsScanning) or nameof(CanUndo)
            or nameof(SourceFolder) or nameof(CurrentFolderFileCount))
        {
            OrganizeCommand.RaiseCanExecuteChanged();
            UndoCommand.RaiseCanExecuteChanged();
            UndoOperationCommand.RaiseCanExecuteChanged();
            ClearHistoryCommand.RaiseCanExecuteChanged();
            AddFolderCommand.RaiseCanExecuteChanged();
            SelectFolderCommand.RaiseCanExecuteChanged();
            RemoveFolderCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task LoadHistoryAsync()
    {
        await _organizer.LoadHistoryAsync();
        RefreshHistory();
    }

    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        SaveSettings();
        _theme.ApplyTheme(IsDarkTheme);
    }

    public void SaveSettings()
    {
        UpdateSettings();
        SettingsService.Save(_settings);
    }

    /// <summary>Persists without blocking the UI thread: folder list changes, auto settings and pauses.</summary>
    private void SaveSettingsInBackground()
    {
        if (_isShutDown) return;
        UpdateSettings();
        _ = SettingsService.SaveInBackground(_settings);
    }

    private void UpdateSettings()
    {
        _settings.IsDarkTheme = IsDarkTheme;
        _settings.SelectedViewMode = SelectedViewMode;
        _settings.Folders = Folders.Select(f => f.Profile).ToList();
    }

    private void LoadSettings()
    {
        _settings = SettingsService.Load();
        // The entry may have been removed (or added) outside the program
        _settings.StartWithWindows = _autostart.IsEnabled;
        _isDarkTheme = _settings.IsDarkTheme;
        _selectedViewMode = _settings.SelectedViewMode;

        foreach (var profile in _settings.Folders)
            Folders.Add(CreateFolderViewModel(profile));
    }

    private FolderViewModel CreateFolderViewModel(FolderProfile profile)
    {
        var folder = new FolderViewModel(profile);
        folder.RefreshStatus(_settings.PauseAll);
        folder.PropertyChanged += OnFolderPropertyChanged;
        return folder;
    }

    /// <summary>Checking a folder scans it into the preview; unchecking drops it.</summary>
    private void OnFolderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(FolderViewModel.IsChecked)) return;
        // With nothing selected the options would edit a throwaway profile: the checked folder
        // becomes the selected one, so they apply to it (SelectFolder rescans the targets)
        if (_selectedFolder == null && !IsOrganizing && sender is FolderViewModel { IsChecked: true } folder)
        {
            SelectFolder(folder);
            return;
        }
        OnTargetsChanged();
        // A running organize works on what it captured; the rescan after it picks up the change
        if (!IsOrganizing)
            _ = SyncScansAsync();
    }

    private void OnTargetsChanged()
    {
        OnPropertyChanged(nameof(OrganizeTargets));
        OnPropertyChanged(nameof(OrganizeButtonText));
        OrganizeCommand.RaiseCanExecuteChanged();
    }

    private void LoadCategories()
    {
        var defaultCategories = FileCategoryService.GetDefaultCategories();
        for (int i = 0; i < defaultCategories.Count; i++)
        {
            var vm = new CategoryViewModel(defaultCategories[i]);
            vm.PropertyChanged += (_, e) =>
            {
                if (_applyingProfile || e.PropertyName != nameof(CategoryViewModel.IsSelected)) return;
                _profile.DisabledCategories.RemoveAll(n => n == vm.Name);
                if (!vm.IsSelected)
                    _profile.DisabledCategories.Add(vm.Name);
                OnProfileEdited();
                RefreshPreviewDeferred();
            };
            Categories.Add(vm);
        }
    }

    /// <summary>
    /// New rules start enabled with one blank "Extension / Is" condition; the rule stays inert
    /// until it has a destination and the condition has a value.
    /// </summary>
    private void AddRule()
    {
        var model = new RoutingRule
        {
            Conditions = [new FilterRule { Field = RuleField.Extension, Operator = RuleOperator.Is }]
        };
        _profile.Rules.Add(model);
        Rules.Add(CreateRuleViewModel(model));
        OnRulesChanged();
    }

    private void RemoveRule(RoutingRuleViewModel rule)
    {
        if (!Rules.Remove(rule)) return;
        rule.Changed -= OnRulesChanged;
        _profile.Rules.Remove(rule.Model);
        OnRulesChanged();
    }

    /// <summary>Moves a rule one step up (-1) or down (+1); rules are evaluated top to bottom.</summary>
    private void MoveRule(RoutingRuleViewModel rule, int delta)
    {
        var index = Rules.IndexOf(rule);
        var newIndex = index + delta;
        if (index < 0 || newIndex < 0 || newIndex >= Rules.Count) return;

        Rules.Move(index, newIndex);
        _profile.Rules.RemoveAt(index);
        _profile.Rules.Insert(newIndex, rule.Model);
        OnRulesChanged();
    }

    private RoutingRuleViewModel CreateRuleViewModel(RoutingRule model)
    {
        var vm = new RoutingRuleViewModel(model);
        vm.Changed += OnRulesChanged;
        return vm;
    }

    /// <summary>Shows the rules of the current profile; no preview refresh (the caller rescans).</summary>
    private void RebuildRules()
    {
        foreach (var rule in Rules)
            rule.Changed -= OnRulesChanged;
        Rules.Clear();
        foreach (var model in _profile.Rules)
            Rules.Add(CreateRuleViewModel(model));
    }

    private void OnRulesChanged()
    {
        OnProfileEdited();
        OnPropertyChanged(nameof(RulesActiveText));
        RaiseAutoWarnings();
        RefreshPreviewDeferred();
    }

    private void AddFolder()
    {
        if (IsOrganizing) return;
        var path = _dialogs.PickFolder("Select Source Folder");
        if (path == null) return;

        var folder = Folders.FirstOrDefault(f => PathKey.Equal(f.FullPath, path));
        if (folder == null)
        {
            folder = CreateFolderViewModel(FolderProfile.CreateDefault(path));
            Folders.Insert(0, folder);
            SaveSettingsInBackground();
        }
        SelectFolder(folder);
    }

    private void RemoveFolder(FolderViewModel folder)
    {
        if (IsOrganizing) return;
        if (ReferenceEquals(folder, _selectedFolder))
            SelectFolder(Folders.FirstOrDefault(f => !ReferenceEquals(f, folder)));
        if (!Folders.Remove(folder)) return;
        folder.PropertyChanged -= OnFolderPropertyChanged;
        if (folder.IsChecked)
        {
            OnTargetsChanged();
            _ = SyncScansAsync();
        }
        if (folder.Profile.Auto.Enabled) ConfigureEngine();
        SaveSettingsInBackground();
        OnPropertyChanged(nameof(KeepRunningInBackground));
        RaiseAutoWarnings();
    }

    private static readonly string[] ProfileProperties =
    [
        nameof(SelectedFolder), nameof(SourceFolder), nameof(HasSourceFolder), nameof(CurrentFolderFileCount),
        nameof(SelectedMethod), nameof(ShowDatePanel), nameof(DestinationSubfolderPattern),
        nameof(SelectedDateGranularity), nameof(ApplyFilters), nameof(DestinationFolder),
        nameof(DestinationDisplay), nameof(CopyMode), nameof(MoveMode), nameof(IncludeSubfolders),
        nameof(SkipDuplicates), nameof(Unmatched), nameof(RulesActiveText)
    ];

    /// <summary>
    /// Shows the folder's profile in the UI; null clears the selection. Without checked folders the
    /// selected folder is the organize target, so it is rescanned. Ignored while organizing.
    /// </summary>
    private void SelectFolder(FolderViewModel? folder)
    {
        if (IsOrganizing || ReferenceEquals(folder, _selectedFolder)) return;

        if (_selectedFolder != null) _selectedFolder.IsSelected = false;
        _selectedFolder = folder;
        if (folder != null) folder.IsSelected = true;
        _profile = folder?.Profile ?? FolderProfile.CreateDefault("");

        _applyingProfile = true;
        try
        {
            foreach (var cat in Categories)
                cat.IsSelected = !_profile.DisabledCategories.Contains(cat.Name);
        }
        finally
        {
            _applyingProfile = false;
        }
        RebuildRules();

        foreach (var name in ProfileProperties)
            OnPropertyChanged(name);
        RaiseAutoProperties();
        OnTargetsChanged();

        _ = SyncScansAsync();
    }

    private void SelectDestinationFolder()
    {
        var folder = _dialogs.PickFolder("Select Destination Folder");
        if (folder != null)
            DestinationFolder = folder;
    }

    private static string ScanKey(FolderViewModel folder) => PathKey.Normalize(folder.FullPath);

    private List<FileItem>? ScannedFiles(FolderViewModel folder) =>
        _scans.GetValueOrDefault(ScanKey(folder));

    private int ScannedCount => _scans.Values.Sum(l => l.Count);

    private bool CanOrganize() =>
        !IsOrganizing && OrganizeTargets.Any(f => ScannedFiles(f) is { Count: > 0 })
        && !OrganizeTargets.Any(f => IsFolderBusy(f.FullPath));

    /// <summary>
    /// Result of the last organize/undo. While set, the rescan and preview refresh that follow
    /// the operation don't overwrite the status bar; cleared by the next user-driven refresh.
    /// </summary>
    private string? _pinnedStatus;

    private void PinStatus(string message)
    {
        _pinnedStatus = message;
        StatusText = message;
    }

    /// <summary>
    /// Brings the per-folder scans in line with <see cref="OrganizeTargets"/>: folders that are no
    /// longer targets are cancelled and dropped, new targets (or every target with
    /// <paramref name="rescanAll"/>) are scanned, then the preview is refreshed.
    /// Completes when the started scans complete.
    /// </summary>
    private async Task SyncScansAsync(bool keepStatus = false, bool rescanAll = false)
    {
        if (!keepStatus) _pinnedStatus = null;

        var targets = OrganizeTargets;
        var targetKeys = targets.Select(ScanKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _scans.Keys.Where(k => !targetKeys.Contains(k)).ToList())
        {
            CancelScan(key);
            _scans.Remove(key);
        }
        IsScanning = _scanCts.Count > 0;

        if (!targets.Any(f => Directory.Exists(f.FullPath)))
        {
            foreach (var key in targetKeys)
            {
                CancelScan(key);
                _scans.Remove(key);
            }
            TotalFiles = 0;
            IsScanning = false;
            OrganizeCommand.RaiseCanExecuteChanged();
            await RefreshPreviewAsync();
            StatusText = _pinnedStatus ?? "Select a folder to start";
            return;
        }

        var toScan = targets.Where(f => rescanAll || !_scans.ContainsKey(ScanKey(f))).ToList();
        if (toScan.Count == 0)
        {
            TotalFiles = ScannedCount;
            OrganizeCommand.RaiseCanExecuteChanged();
            await RefreshPreviewAsync();
            return;
        }

        await Task.WhenAll(toScan.Select(ScanFolderAsync));
    }

    private void CancelScan(string key)
    {
        if (!_scanCts.Remove(key, out var cts)) return;
        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>
    /// (Re)scans one folder into its own list, non-recursive or recursive per its own profile.
    /// Each scan has its own cancellation and list, so a batch posted by a cancelled or older scan
    /// never lands in the current list.
    /// </summary>
    private async Task ScanFolderAsync(FolderViewModel folder)
    {
        var key = ScanKey(folder);
        CancelScan(key);
        var files = new List<FileItem>();
        _scans[key] = files;

        if (!Directory.Exists(folder.FullPath))
        {
            OnFolderScanned(folder, files);
            IsScanning = _scanCts.Count > 0;
            await RefreshPreviewAsync();
            return;
        }

        var cts = new CancellationTokenSource();
        _scanCts[key] = cts;
        var ct = cts.Token;
        TotalFiles = ScannedCount;
        IsScanning = true;
        if (_pinnedStatus == null) StatusText = "Scanning...";

        try
        {
            await _organizer.ScanFolderAsync(folder.FullPath, folder.Profile.IncludeSubfolders, async batch =>
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    // A batch posted before cancellation may land after a newer scan replaced the list
                    if (ct.IsCancellationRequested) return;

                    files.AddRange(batch);
                    OnFolderScanned(folder, files);
                    if (_pinnedStatus == null) StatusText = $"Scanning... {ScannedCount} files found";
                    RefreshPreviewThrottled();
                });
            }, 50, ct);
        }
        catch (OperationCanceledException)
        {
            // Scan was cancelled
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The folder vanished or became unreadable mid-scan — keep what was found
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                _scanCts.Remove(key);
                cts.Dispose();
                OnFolderScanned(folder, files);
                if (_scanCts.Count == 0)
                {
                    if (_pinnedStatus == null) StatusText = $"{ScannedCount} files found";
                    IsScanning = false;
                    // A throttled refresh queued by the last batch will run within 200ms anyway —
                    // don't do the full recomputation twice. (Not RefreshPreviewDeferred: this
                    // refresh isn't user-driven and must keep a pinned status.)
                    if (!_throttlePending)
                        _ = RefreshPreviewAsync();
                }
            }
        }
    }

    private void OnFolderScanned(FolderViewModel folder, List<FileItem> files)
    {
        folder.FileCount = files.Count;
        if (ReferenceEquals(folder, _selectedFolder))
            OnPropertyChanged(nameof(CurrentFolderFileCount));
        TotalFiles = ScannedCount;
        OrganizeCommand.RaiseCanExecuteChanged();
    }

    private int _refreshVersion;
    private Task? _refreshTask;

    /// <summary>Preview refresh after a user change (filters, method, template…).</summary>
    private void RefreshPreviewDeferred()
    {
        _pinnedStatus = null;
        _ = RefreshPreviewAsync();
    }

    /// <summary>
    /// Throttled refresh — runs at most once per 200ms while calls keep coming.
    /// Used during scanning so the preview updates live without refreshing on every batch.
    /// </summary>
    private async void RefreshPreviewThrottled()
    {
        if (_throttlePending) return;
        _throttlePending = true;
        try
        {
            await Task.Delay(200); // resumes on the UI thread
        }
        finally
        {
            _throttlePending = false;
        }
        _ = RefreshPreviewAsync();
    }

    private async Task RefreshPreviewAsync()
    {
        var version = ++_refreshVersion;

        // Wait for previous refresh to finish before starting new one
        if (_refreshTask is { IsCompleted: false })
            await _refreshTask;

        // If another refresh was requested while waiting, bail out
        if (version != _refreshVersion) return;

        _refreshTask = RefreshPreviewCore();
        await _refreshTask;
    }

    private async Task RefreshPreviewCore()
    {
        try
        {
        // Save expanded state on UI thread
        var expandedNames = new HashSet<string>();
        foreach (var item in PreviewItems)
            CollectExpandedNames(item, expandedNames);

        // Snapshot on UI thread (each target with its own profile) to avoid races with scan callbacks
        var categories = Categories.Select(c => c.Category).ToList();
        var sources = OrganizeTargets
            .Select(f => new PreviewSource(f.Name, OrganizeRequest.FromProfile(f.Profile, categories),
                new List<FileItem>(ScannedFiles(f) ?? [])))
            .ToList();
        int scanned = sources.Sum(s => s.Files.Count);

        if (scanned == 0)
        {
            _thumbnails.CancelPending();
            PreviewItems.Clear();
            CategoryStatsItems.Clear();
            _groupCount = 0;
            ViewFileGroups.Clear();
            RebuildFlatViewItems();
            return;
        }

        Func<ViewFileItem, object?> thumbnailProvider = RequestThumbnail;

        // Heavy work on background thread
        var result = await Task.Run(() => PreviewBuilder.Build(sources, CategoryColors, thumbnailProvider));

        // Update UI
        PreviewItems.Clear();
        CategoryStatsItems.Clear();

        // Preserve view group expanded states
        var viewExpandedStates = new Dictionary<string, bool>();
        foreach (var g in ViewFileGroups)
            viewExpandedStates[g.Key] = g.IsExpanded;
        ViewFileGroups.Clear();
        _thumbnails.CancelPending();

        foreach (var root in result.Roots)
        {
            RestoreExpandedState(root, expandedNames);
            PreviewItems.Add(root);
        }

        foreach (var stat in result.Stats)
            CategoryStatsItems.Add(stat);

        foreach (var vg in result.ViewGroups)
        {
            if (viewExpandedStates.TryGetValue(vg.Key, out var wasExpanded))
                vg.IsExpanded = wasExpanded;
            ViewFileGroups.Add(vg);
        }

        // Build flat list for virtualized ListBox; thumbnails load lazily for visible rows.
        // Replacing it recreates every visible row (hundreds of ms of layout), so while scanning
        // it is rebuilt at most every FlatListScanInterval; the refresh after the scan always does.
        if (!IsScanning || DateTime.UtcNow - _lastFlatRebuild >= FlatListScanInterval)
            RebuildFlatViewItems();

        _groupCount = result.GroupCount;
        TotalFiles = result.FileCount;
        OnPropertyChanged(nameof(TypeCountText));
        OnPropertyChanged(nameof(RulesActiveText));
        StatusText = _pinnedStatus ?? $"{result.FileCount} of {scanned} files will be organized";
        }
        catch (Exception)
        {
            // Prevent unobserved task exceptions from crashing the app
        }
    }

    /// <summary>
    /// Organizes every target in list order, each with its own profile, as one run. A folder that
    /// fails doesn't stop the others; the status reports each folder.
    /// </summary>
    private async Task OrganizeAsync()
    {
        if (IsOrganizing) return;

        // Everything is captured before the first await: the user may flip options, check or
        // select other folders while files are being moved.
        var categories = Categories.Select(c => c.Category).ToList();
        var jobs = OrganizeTargets
            .Select(f => (f.Name, Request: OrganizeRequest.FromProfile(f.Profile, categories),
                Files: (IReadOnlyList<FileItem>)(ScannedFiles(f)?.ToList() ?? [])))
            .ToList();
        if (jobs.Count == 0) return;
        var run = RunContext.NewManual();

        IsOrganizing = true;
        Progress = 0;
        StatusText = "Organizing files...";

        try
        {
            var parts = new List<string>();
            for (int i = 0; i < jobs.Count; i++)
            {
                var (name, request, files) = jobs[i];
                var index = i;
                var progressReporter = new Progress<int>(p => Progress = (index * 100 + p) / jobs.Count);
                // Auto-organize may have started on this folder since the command was enabled
                if (!_gate.TryEnter(request.SourceFolder))
                {
                    parts.Add(jobs.Count == 1 ? "Folder is busy (auto-organize is running), try again" : $"{name}: busy, skipped");
                    continue;
                }
                try
                {
                    if (!Directory.Exists(request.SourceFolder))
                        throw new DirectoryNotFoundException("folder not found");

                    var result = await _organizer.OrganizeAsync(files, request, run, progressReporter, _organizeCts.Token);
                    var summary = Summary(result, request.CopyMode);
                    parts.Add(jobs.Count == 1 ? $"Done! {summary.Moved} files {summary.Details}" : $"{name}: {summary.Moved} {summary.Details}");
                }
                catch (Exception ex)
                {
                    if (jobs.Count == 1)
                    {
                        parts.Add($"Error: {ex.Message}");
                        _dialogs.ShowError("Error", ex.Message);
                    }
                    else
                    {
                        parts.Add($"{name}: error: {ex.Message}");
                    }
                }
                finally
                {
                    _gate.Exit(request.SourceFolder);
                }
            }

            PinStatus(string.Join("; ", parts));
            OnPropertyChanged(nameof(CanUndo));
            RefreshHistory();
            await SyncScansAsync(keepStatus: true, rescanAll: true);
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            _dialogs.ShowError("Error", ex.Message);
        }
        finally
        {
            IsOrganizing = false;
            Progress = 0;
        }
    }

    /// <summary>"12" and "moved, 2 duplicates skipped, 1 failed" for the status bar.</summary>
    private static (int Moved, string Details) Summary(OrganizeOperation result, bool copyMode)
    {
        var action = copyMode ? "copied" : "moved";
        var dupeInfo = result.SkippedDuplicates > 0
            ? copyMode
                ? $", {result.SkippedDuplicates} duplicates skipped"
                : $", {result.SkippedDuplicates} duplicates moved to {FileOrganizerService.DuplicatesFolderName}"
            : "";
        var failInfo = result.FailedFiles > 0 ? $", {result.FailedFiles} failed" : "";
        return (result.Moves.Count(m => !m.WasDuplicate), $"{action}{dupeInfo}{failInfo}");
    }

    /// <summary>
    /// Undoes the whole last run. Undoing a later operation of the run can make an earlier one
    /// undoable (the later one had moved its outputs), so the run's undoable operations are
    /// re-evaluated until none is left.
    /// </summary>
    private Task UndoLastAsync()
    {
        var last = _organizer.LastUndoableRun;
        if (last.Count == 0) return Task.CompletedTask;
        var runId = last[0].RunId;
        if (runId == Guid.Empty) return UndoOperationsAsync(() => last);

        return UndoOperationsAsync(() => _organizer.History
            .Where(op => op.RunId == runId && _organizer.CanUndoOperation(op))
            .Reverse()
            .ToList());
    }

    private Task UndoOperationAsync(OrganizeOperation? operation) =>
        operation == null ? Task.CompletedTask : UndoOperationsAsync(() => [operation]);

    /// <summary>
    /// Undoes the operations <paramref name="next"/> returns (newest first), asking again after each
    /// pass until it returns nothing new; each operation is attempted at most once.
    /// </summary>
    private async Task UndoOperationsAsync(Func<IReadOnlyList<OrganizeOperation>> next)
    {
        if (IsOrganizing) return;
        var first = next();
        if (first.Count == 0 || !_organizer.CanUndoOperation(first[0])) return;

        IsOrganizing = true;
        StatusText = "Undoing operation...";
        Progress = 0;

        var undone = new List<OrganizeOperation>();
        var attempted = new HashSet<OrganizeOperation>();
        try
        {
            int restored = 0, failed = 0, busy = 0;
            for (var batch = first; batch.Count > 0; batch = next().Where(op => !attempted.Contains(op)).ToList())
            {
                foreach (var operation in batch)
                {
                    attempted.Add(operation);
                    if (!_organizer.CanUndoOperation(operation)) continue;
                    // Not while auto-organize works on the folder (legacy entries have no folder)
                    var source = operation.SourceFolder;
                    var gated = !string.IsNullOrWhiteSpace(source);
                    if (gated && !_gate.TryEnter(source))
                    {
                        busy++;
                        continue;
                    }
                    try
                    {
                        // Pause before the files come back and before the gate is released, or a pending
                        // auto run (schedule, debounce, the undo's own watcher events) would re-sort them
                        if (gated) PauseAutoForUndo(source);
                        var progressReporter = new Progress<int>(p => Progress = p);
                        var result = await _organizer.UndoOperationAsync(operation, progressReporter);
                        restored += result.Restored;
                        failed += result.Failed;
                        if (result.Restored > 0 || operation.IsUndone) undone.Add(operation);
                    }
                    finally
                    {
                        if (gated) _gate.Exit(source);
                    }
                }
            }

            var busyInfo = busy > 0 ? $"; {busy} skipped (folder busy), try again" : "";
            PinStatus((failed == 0
                ? $"Undo complete! {restored} files restored"
                : $"Undo partially complete: {restored} files restored, {failed} could not be restored") + busyInfo);
            OnPropertyChanged(nameof(CanUndo));
            RefreshHistory();
            await SyncScansAsync(keepStatus: true, rescanAll: true);
        }
        catch (Exception ex)
        {
            StatusText = $"Undo error: {ex.Message}";
        }
        finally
        {
            IsOrganizing = false;
            Progress = 0;
            if (undone.Count > 0) OperationsUndone?.Invoke(undone);
        }
    }

    private static void CollectExpandedNames(PreviewItem item, HashSet<string> names, string path = "")
    {
        var fullPath = string.IsNullOrEmpty(path) ? item.Name : $"{path}/{item.Name}";
        if (item.IsFolder && !item.IsExpanded)
            names.Add(fullPath);
        foreach (var child in item.Children)
            CollectExpandedNames(child, names, fullPath);
    }

    private static void RestoreExpandedState(PreviewItem item, HashSet<string> collapsedNames, string path = "")
    {
        var fullPath = string.IsNullOrEmpty(path) ? item.Name : $"{path}/{item.Name}";
        if (item.IsFolder)
            item.IsExpanded = !collapsedNames.Contains(fullPath);
        foreach (var child in item.Children)
            RestoreExpandedState(child, collapsedNames, fullPath);
    }

    private object? RequestThumbnail(ViewFileItem item) =>
        _thumbnails.GetOrRequest(item.FullPath, item.DateModified, ThumbnailSize, image => item.Thumbnail = image);

    private void SetPreviewExpanded(bool expanded)
    {
        foreach (var item in PreviewItems)
            SetPreviewItemExpanded(item, expanded);
    }

    private static void SetPreviewItemExpanded(PreviewItem item, bool expanded)
    {
        if (item.IsFolder)
            item.IsExpanded = expanded;
        foreach (var child in item.Children)
            SetPreviewItemExpanded(child, expanded);
    }

    private void SetViewGroupsExpanded(bool expanded)
    {
        foreach (var group in ViewFileGroups)
            group.IsExpanded = expanded;
    }

    /// <summary>Least time between file list rebuilds while a scan is running.</summary>
    internal static TimeSpan FlatListScanInterval { get; set; } = TimeSpan.FromSeconds(2);

    private DateTime _lastFlatRebuild;

    private void RebuildFlatViewItems()
    {
        _lastFlatRebuild = DateTime.UtcNow;
        var flat = new List<ViewFileItem>();
        foreach (var vg in ViewFileGroups)
        {
            flat.Add(new ViewFileItem
            {
                IsHeader = true,
                Group = vg,
                HeaderText = vg.HeaderText,
                Icon = vg.IsExpanded ? "\uE76C" : "\uE76B"
            });
            if (vg.IsExpanded)
                flat.AddRange(vg.Files);
        }
        FlatViewItems = flat;
        OnPropertyChanged(nameof(FlatViewItems));
    }

    private void RefreshHistory()
    {
        HistoryItems.Clear();
        foreach (var op in _organizer.History.Reverse())
            HistoryItems.Add(op);
        OnPropertyChanged(nameof(CanUndo));
    }
}
