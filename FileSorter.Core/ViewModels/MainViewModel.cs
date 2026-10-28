using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using FileSorter.Abstractions;
using FileSorter.Models;
using FileSorter.Services;

namespace FileSorter.ViewModels;

public class MainViewModel : ObservableObject
{
    private const int ThumbnailSize = 48;

    private readonly FileOrganizerService _organizer = new();
    private readonly IUiDispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly IThumbnailProvider _thumbnails;
    private readonly IThemeService _theme;
    private CancellationTokenSource? _scanCts;
    private bool _isScanning;

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

    public int CurrentFolderFileCount
    {
        get => _selectedFolder?.FileCount ?? 0;
        private set
        {
            if (_selectedFolder == null || _selectedFolder.FileCount == value) return;
            _selectedFolder.FileCount = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Writes a per-folder option into the selected profile; true if it changed.</summary>
    private bool SetProfileValue<T>(T current, T value, Action<FolderProfile, T> assign,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        assign(_profile, value);
        OnPropertyChanged(propertyName);
        return true;
    }

    public string? DestinationFolder
    {
        get => _profile.DestinationFolder;
        set
        {
            if (SetProfileValue(_profile.DestinationFolder, value, (p, v) => p.DestinationFolder = v))
                OnPropertyChanged(nameof(DestinationDisplay));
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
            if (SetProfileValue(_profile.IncludeSubfolders, value, (p, v) => p.IncludeSubfolders = v))
                _ = RefreshFilesAsync();
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
    public string TypeCountText => SelectedMethod switch
    {
        OrganizeMethod.ByType => $"{CategoryStatsItems.Count} Types",
        OrganizeMethod.ByExtension => $"{CategoryStatsItems.Count} Extensions",
        OrganizeMethod.ByDate => $"{CategoryStatsItems.Count} Dates",
        OrganizeMethod.ByAlphabet => $"{CategoryStatsItems.Count} Letters",
        OrganizeMethod.BySize => $"{CategoryStatsItems.Count} Sizes",
        OrganizeMethod.ByDateTaken => $"{CategoryStatsItems.Count} Dates",
        OrganizeMethod.ByName => $"{CategoryStatsItems.Count} Names",
        _ => $"{CategoryStatsItems.Count} Groups"
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

    public ICommand AddFolderCommand { get; }
    public ICommand SelectDestinationCommand { get; }
    public AsyncRelayCommand OrganizeCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand RemoveRuleCommand { get; }
    public ICommand MoveRuleUpCommand { get; }
    public ICommand MoveRuleDownCommand { get; }
    public ICommand BrowseRuleDestinationCommand { get; }
    public ICommand SetMethodCommand { get; }
    public ICommand SelectFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }
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

    private List<FileItem> _scannedFiles = [];

    public MainViewModel(IUiDispatcher dispatcher, IDialogService dialogs,
        IThumbnailProvider thumbnails, IThemeService theme)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _thumbnails = thumbnails;
        _theme = theme;

        AddFolderCommand = new RelayCommand(_ => AddFolder());
        SelectDestinationCommand = new RelayCommand(_ => SelectDestinationFolder());
        OrganizeCommand = new AsyncRelayCommand(_ => OrganizeAsync(), _ => CanOrganize());
        UndoCommand = new AsyncRelayCommand(_ => UndoLastAsync(), _ => !IsOrganizing && CanUndo);
        UndoOperationCommand = new AsyncRelayCommand(
            p => UndoOperationAsync(p as OrganizeOperation),
            p => !IsOrganizing && p is OrganizeOperation op && _organizer.CanUndoOperation(op));
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
        });
        RemoveFolderCommand = new RelayCommand(p =>
        {
            if (p is FolderViewModel f)
                RemoveFolder(f);
        });
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
        _settings.IsDarkTheme = IsDarkTheme;
        _settings.SelectedViewMode = SelectedViewMode;
        _settings.Folders = Folders.Select(f => f.Profile).ToList();
        SettingsService.Save(_settings);
    }

    private void LoadSettings()
    {
        _settings = SettingsService.Load();
        _isDarkTheme = _settings.IsDarkTheme;
        _selectedViewMode = _settings.SelectedViewMode;

        foreach (var profile in _settings.Folders)
            Folders.Add(CreateFolderViewModel(profile));
    }

    private FolderViewModel CreateFolderViewModel(FolderProfile profile)
    {
        var folder = new FolderViewModel(profile);
        folder.RefreshStatus(_settings.PauseAll);
        return folder;
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
        OnPropertyChanged(nameof(RulesActiveText));
        RefreshPreviewDeferred();
    }

    private void AddFolder()
    {
        var path = _dialogs.PickFolder("Select Source Folder");
        if (path == null) return;

        var folder = Folders.FirstOrDefault(f => PathKey.Equal(f.FullPath, path));
        if (folder == null)
        {
            folder = CreateFolderViewModel(FolderProfile.CreateDefault(path));
            Folders.Insert(0, folder);
        }
        SelectFolder(folder);
    }

    private void RemoveFolder(FolderViewModel folder)
    {
        if (ReferenceEquals(folder, _selectedFolder))
            SelectFolder(Folders.FirstOrDefault(f => !ReferenceEquals(f, folder)));
        Folders.Remove(folder);
    }

    private static readonly string[] ProfileProperties =
    [
        nameof(SelectedFolder), nameof(SourceFolder), nameof(HasSourceFolder), nameof(CurrentFolderFileCount),
        nameof(SelectedMethod), nameof(ShowDatePanel), nameof(DestinationSubfolderPattern),
        nameof(SelectedDateGranularity), nameof(ApplyFilters), nameof(DestinationFolder),
        nameof(DestinationDisplay), nameof(CopyMode), nameof(MoveMode), nameof(IncludeSubfolders),
        nameof(SkipDuplicates), nameof(Unmatched), nameof(RulesActiveText)
    ];

    /// <summary>Shows the folder's profile in the UI and rescans; null clears the selection.</summary>
    private void SelectFolder(FolderViewModel? folder)
    {
        if (ReferenceEquals(folder, _selectedFolder)) return;

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

        _ = RefreshFilesAsync();
    }

    private void SelectDestinationFolder()
    {
        var folder = _dialogs.PickFolder("Select Destination Folder");
        if (folder != null)
            DestinationFolder = folder;
    }

    private bool CanOrganize() =>
        !string.IsNullOrEmpty(SourceFolder) && _scannedFiles.Count > 0 && !IsOrganizing;

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

    private async Task RefreshFilesAsync(bool keepStatus = false)
    {
        if (!keepStatus) _pinnedStatus = null;

        // Cancel previous scan
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        if (string.IsNullOrEmpty(SourceFolder) || !Directory.Exists(SourceFolder))
        {
            _scannedFiles = [];
            TotalFiles = 0;
            IsScanning = false;
            await RefreshPreviewAsync();
            StatusText = "Select a folder to start";
            return;
        }

        _scannedFiles = [];
        TotalFiles = 0;
        IsScanning = true;
        if (_pinnedStatus == null) StatusText = "Scanning...";

        try
        {
            await _organizer.ScanFolderAsync(SourceFolder, IncludeSubfolders, async batch =>
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    // A batch posted before cancellation may land after a newer scan reset the list
                    if (ct.IsCancellationRequested) return;

                    _scannedFiles.AddRange(batch);
                    TotalFiles = _scannedFiles.Count;
                    CurrentFolderFileCount = _scannedFiles.Count;
                    if (_pinnedStatus == null) StatusText = $"Scanning... {_scannedFiles.Count} files found";
                    RefreshPreviewThrottled();
                });
            }, 50, ct);
        }
        catch (OperationCanceledException)
        {
            // Scan was cancelled
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                TotalFiles = _scannedFiles.Count;
                CurrentFolderFileCount = _scannedFiles.Count;
                if (_pinnedStatus == null) StatusText = $"{_scannedFiles.Count} files found";
                IsScanning = false;
                // A throttled refresh queued by the last batch will run within 200ms anyway —
                // don't do the full recomputation twice. (Not RefreshPreviewDeferred: this
                // refresh isn't user-driven and must keep a pinned status.)
                if (!_throttlePending)
                    _ = RefreshPreviewAsync();
            }
        }
    }

    /// <summary>Immutable snapshot of the selected folder's profile, safe to use from a background thread.</summary>
    private OrganizeRequest CaptureRequest() =>
        OrganizeRequest.FromProfile(_profile, Categories.Select(c => c.Category).ToList());

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

        if (_scannedFiles.Count == 0)
        {
            _thumbnails.CancelPending();
            PreviewItems.Clear();
            CategoryStatsItems.Clear();
            ViewFileGroups.Clear();
            RebuildFlatViewItems();
            return;
        }

        // Snapshot on UI thread to avoid race with scan callback
        var files = new List<FileItem>(_scannedFiles);
        var request = CaptureRequest();
        var activeCategories = request.Categories;
        var allowedCategories = request.AllowedCategories;
        var template = request.Template;
        var method = request.Method;
        var destDisplay = DestinationDisplay;
        Func<ViewFileItem, object?> thumbnailProvider = RequestThumbnail;

        // Heavy work on background thread
        var result = await Task.Run(() =>
        {
            var preview = _organizer.GeneratePreview(files, request);
            var filteredFiles = new List<FileItem>();
            foreach (var item in preview)
                CollectFiles(item, filteredFiles);

            // Build root
            var root = new PreviewItem
            {
                Name = $"Destination \u2192 {destDisplay}",
                IsFolder = true,
                Icon = "\uE8B7"
            };
            foreach (var item in preview) root.Children.Add(item);

            // Build stats
            var stats = new List<CategoryStats>();
            int total = filteredFiles.Count;
            double currentAngle = 0;
            int colorIndex = 0;

            if (method == OrganizeMethod.ByType && template == null)
            {
                var allCatCounts = new Dictionary<string, int>();
                foreach (var file in files)
                {
                    var catName = FileCategoryService.GetCategoryName(file.Extension, activeCategories);
                    allCatCounts.TryGetValue(catName, out var count);
                    allCatCounts[catName] = count + 1;
                }

                foreach (var kv in allCatCounts.OrderBy(k => k.Key))
                {
                    bool isSelected = allowedCategories == null || allowedCategories.Contains(kv.Key);
                    int groupCount = isSelected
                        ? filteredFiles.Count(f => FileCategoryService.GetCategoryName(f.Extension, activeCategories) == kv.Key)
                        : 0;
                    double pct = isSelected && total > 0 ? (double)groupCount / total : 0;
                    double sweep = pct * 360;
                    var color = CategoryColors[colorIndex % CategoryColors.Length];

                    stats.Add(new CategoryStats
                    {
                        Name = kv.Key, Count = kv.Value, Color = color,
                        Percentage = pct * 100, StartAngle = currentAngle,
                        SweepAngle = sweep, IsSelected = isSelected
                    });

                    if (isSelected) currentAngle += sweep;
                    colorIndex++;
                }
            }
            else
            {
                foreach (var group in preview.OrderBy(g => g.Name))
                {
                    int groupFileCount = CountFiles(group);
                    double pct = total > 0 ? (double)groupFileCount / total : 0;
                    double sweep = pct * 360;
                    var color = CategoryColors[colorIndex % CategoryColors.Length];

                    stats.Add(new CategoryStats
                    {
                        Name = group.Name, Count = groupFileCount, Color = color,
                        Percentage = pct * 100, StartAngle = currentAngle,
                        SweepAngle = sweep, IsSelected = true
                    });

                    currentAngle += sweep;
                    colorIndex++;
                }
            }

            // Build view file groups (same grouping as tree)
            var viewGroups = BuildViewFileGroups(preview, activeCategories, thumbnailProvider);

            return (root, stats, filteredFiles.Count, viewGroups);
        });

        // Update UI
        PreviewItems.Clear();
        CategoryStatsItems.Clear();

        // Preserve view group expanded states
        var viewExpandedStates = new Dictionary<string, bool>();
        foreach (var g in ViewFileGroups)
            viewExpandedStates[g.Key] = g.IsExpanded;
        ViewFileGroups.Clear();
        _thumbnails.CancelPending();

        RestoreExpandedState(result.root, expandedNames);
        PreviewItems.Add(result.root);

        foreach (var stat in result.stats)
            CategoryStatsItems.Add(stat);

        foreach (var vg in result.viewGroups)
        {
            if (viewExpandedStates.TryGetValue(vg.Key, out var wasExpanded))
                vg.IsExpanded = wasExpanded;
            ViewFileGroups.Add(vg);
        }

        // Build flat list for virtualized ListBox; thumbnails load lazily for visible rows
        RebuildFlatViewItems();

        TotalFiles = result.Count;
        OnPropertyChanged(nameof(TypeCountText));
        OnPropertyChanged(nameof(RulesActiveText));
        StatusText = _pinnedStatus ?? $"{result.Count} of {_scannedFiles.Count} files will be organized";
        }
        catch (Exception)
        {
            // Prevent unobserved task exceptions from crashing the app
        }
    }

    private async Task OrganizeAsync()
    {
        IsOrganizing = true;
        Progress = 0;
        StatusText = "Organizing files...";

        try
        {
            // Captured before the await: the user may flip options while files are being moved
            var request = CaptureRequest();
            var copyMode = request.CopyMode;
            var progressReporter = new Progress<int>(p => Progress = p);

            var result = await _organizer.OrganizeAsync(_scannedFiles.ToList(), request, RunContext.NewManual(), progressReporter);

            var action = copyMode ? "copied" : "moved";
            var movedCount = result.Moves.Count(m => !m.WasDuplicate);
            var dupeInfo = result.SkippedDuplicates > 0
                ? copyMode
                    ? $", {result.SkippedDuplicates} duplicates skipped"
                    : $", {result.SkippedDuplicates} duplicates moved to {FileOrganizerService.DuplicatesFolderName}"
                : "";
            var failInfo = result.FailedFiles > 0 ? $", {result.FailedFiles} failed" : "";
            PinStatus($"Done! {movedCount} files {action}{dupeInfo}{failInfo}");
            OnPropertyChanged(nameof(CanUndo));
            RefreshHistory();
            await RefreshFilesAsync(keepStatus: true);
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

    private Task UndoLastAsync() => UndoOperationsAsync(_organizer.LastUndoableRun.Reverse().ToList());

    private Task UndoOperationAsync(OrganizeOperation? operation) =>
        operation == null ? Task.CompletedTask : UndoOperationsAsync([operation]);

    /// <summary>Undoes the given operations in the order supplied (callers pass newest first).</summary>
    private async Task UndoOperationsAsync(IReadOnlyList<OrganizeOperation> operations)
    {
        if (operations.Count == 0 || !_organizer.CanUndoOperation(operations[0])) return;

        IsOrganizing = true;
        StatusText = "Undoing operation...";
        Progress = 0;

        var undone = new List<OrganizeOperation>();
        try
        {
            int restored = 0, failed = 0;
            foreach (var operation in operations)
            {
                if (!_organizer.CanUndoOperation(operation)) continue;
                var progressReporter = new Progress<int>(p => Progress = p);
                var result = await _organizer.UndoOperationAsync(operation, progressReporter);
                restored += result.Restored;
                failed += result.Failed;
                if (result.Restored > 0 || operation.IsUndone) undone.Add(operation);
            }

            PinStatus(failed == 0
                ? $"Undo complete! {restored} files restored"
                : $"Undo partially complete: {restored} files restored, {failed} could not be restored");
            OnPropertyChanged(nameof(CanUndo));
            RefreshHistory();
            await RefreshFilesAsync(keepStatus: true);
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

    private static void CollectFiles(PreviewItem item, List<FileItem> result)
    {
        if (item.File is { } file) result.Add(file);
        foreach (var child in item.Children)
            CollectFiles(child, result);
    }

    private static int CountFiles(PreviewItem item)
    {
        if (!item.IsFolder) return 1;
        int count = 0;
        foreach (var child in item.Children)
            count += CountFiles(child);
        return count;
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

    private static List<ViewFileGroup> BuildViewFileGroups(
        ObservableCollection<PreviewItem> preview, IReadOnlyList<FileCategory> categories,
        Func<ViewFileItem, object?> thumbnailProvider)
    {
        var result = new List<ViewFileGroup>();

        foreach (var group in preview)
        {
            var files = new List<ViewFileItem>();
            FlattenPreviewGroup(group, files, categories, thumbnailProvider);
            result.Add(new ViewFileGroup
            {
                Key = group.Name,
                HeaderText = $"{group.Name}  ({files.Count})",
                FileCount = files.Count,
                Files = files
            });
        }

        return result;
    }

    private static void FlattenPreviewGroup(PreviewItem item, List<ViewFileItem> result,
        IReadOnlyList<FileCategory> categories, Func<ViewFileItem, object?> thumbnailProvider)
    {
        foreach (var child in item.Children)
        {
            if (child.IsFolder)
            {
                FlattenPreviewGroup(child, result, categories, thumbnailProvider);
            }
            else if (child.File is { } file)
            {
                var catName = FileCategoryService.GetCategoryName(file.Extension, categories);

                result.Add(new ViewFileItem
                {
                    FileName = file.FileName,
                    Extension = file.Extension,
                    Icon = GetCategoryIcon(catName),
                    SizeText = FormatSize(file.Size),
                    DateText = file.DateModified.ToString("MMM dd, yyyy HH:mm"),
                    FullPath = file.FullPath,
                    DateModified = file.DateModified,
                    CategoryName = catName,
                    HasThumbnailSupport = HasThumbnailSupport(file.Extension),
                    ThumbnailProvider = thumbnailProvider
                });
            }
        }
    }

    private object? RequestThumbnail(ViewFileItem item) =>
        _thumbnails.GetOrRequest(item.FullPath, item.DateModified, ThumbnailSize, image => item.Thumbnail = image);

    private static string GetCategoryIcon(string categoryName) => categoryName switch
    {
        "Documents" => "\uE8A5",
        "Images" => "\uE8B9",
        "Videos" => "\uE8B2",
        "Music" => "\uE8D6",
        "Archives" => "\uE8C8",
        "Code" => "\uE943",
        "Executables" => "\uE756",
        "Fonts" => "\uE8D2",
        _ => "\uE7C3"
    };

    private static readonly HashSet<string> ThumbnailExtensions =
    [
        // Images
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".ico", ".tiff", ".tif", ".webp", ".heic", ".heif", ".svg",
        // Documents
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf",
        // Videos
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".mpg", ".mpeg",
        // Other
        ".psd", ".ai", ".eps", ".html", ".htm"
    ];

    private static bool HasThumbnailSupport(string ext) =>
        ThumbnailExtensions.Contains(ext.ToLowerInvariant());

    private static string FormatSize(long size) => size switch
    {
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{size / (1024.0 * 1024):F1} MB",
        _ => $"{size / (1024.0 * 1024 * 1024):F2} GB"
    };

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

    private void RebuildFlatViewItems()
    {
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
