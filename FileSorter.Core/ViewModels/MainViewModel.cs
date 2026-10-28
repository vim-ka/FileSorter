using System.Collections.ObjectModel;
using System.ComponentModel;
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

    private string _sourceFolder = string.Empty;
    private string? _destinationFolder;
    private string _currentFolderName = string.Empty;
    private int _currentFolderFileCount;
    private OrganizeMethod _selectedMethod = OrganizeMethod.ByType;
    private bool _includeSubfolders = true;
    private bool _copyMode;
    private bool _moveMode = true;
    private bool _applyRules;
    private bool _applyFilters = true;
    private bool _rulesAndMode;
    private bool _rulesOrMode = true;
    private bool _isOrganizing;
    private int _progress;
    private string _statusText = "Select a folder to start";
    private int _totalFiles;
    private string _destinationSubfolderPattern = "";
    private bool _isDarkTheme = true;
    private bool _showHistory;
    private DateGranularity _selectedDateGranularity = DateGranularity.Month;
    private FileViewMode _selectedViewMode = FileViewMode.Tree;
    private bool _skipDuplicates;
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

    public string SourceFolder
    {
        get => _sourceFolder;
        set
        {
            if (SetProperty(ref _sourceFolder, value))
            {
                var name = string.IsNullOrEmpty(value) ? "" : Path.GetFileName(value);
                CurrentFolderName = string.IsNullOrEmpty(name) ? value : name;
                _ = RefreshFilesAsync();
                OnPropertyChanged(nameof(HasSourceFolder));
            }
        }
    }

    public bool HasSourceFolder => !string.IsNullOrEmpty(_sourceFolder);

    public string CurrentFolderName
    {
        get => _currentFolderName;
        set => SetProperty(ref _currentFolderName, value);
    }

    public int CurrentFolderFileCount
    {
        get => _currentFolderFileCount;
        set => SetProperty(ref _currentFolderFileCount, value);
    }

    public ObservableCollection<RecentFolder> RecentFolders { get; } = [];

    public string? DestinationFolder
    {
        get => _destinationFolder;
        set
        {
            if (SetProperty(ref _destinationFolder, value))
                OnPropertyChanged(nameof(DestinationDisplay));
        }
    }

    public string DestinationDisplay =>
        string.IsNullOrEmpty(_destinationFolder) ? "Folder" : Path.GetFileName(_destinationFolder) ?? "Folder";

    public string DestinationSubfolderPattern
    {
        get => _destinationSubfolderPattern;
        set
        {
            if (SetProperty(ref _destinationSubfolderPattern, value))
                RefreshPreviewDeferred();
        }
    }

    public OrganizeMethod SelectedMethod
    {
        get => _selectedMethod;
        set
        {
            if (SetProperty(ref _selectedMethod, value))
            {
                OnPropertyChanged(nameof(ShowDatePanel));
                RefreshPreviewDeferred();
            }
        }
    }

    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set
        {
            if (SetProperty(ref _includeSubfolders, value))
                _ = RefreshFilesAsync();
        }
    }

    public bool CopyMode
    {
        get => _copyMode;
        set
        {
            if (SetProperty(ref _copyMode, value) && value)
                MoveMode = false;
        }
    }

    public bool MoveMode
    {
        get => _moveMode;
        set
        {
            if (SetProperty(ref _moveMode, value) && value)
                CopyMode = false;
        }
    }

    public bool ApplyRules
    {
        get => _applyRules;
        set
        {
            if (SetProperty(ref _applyRules, value))
            {
                RefreshPreviewDeferred();
                OnPropertyChanged(nameof(RulesActiveText));
            }
        }
    }

    public bool ApplyFilters
    {
        get => _applyFilters;
        set
        {
            if (SetProperty(ref _applyFilters, value))
                RefreshPreviewDeferred();
        }
    }

    public bool SkipDuplicates
    {
        get => _skipDuplicates;
        set => SetProperty(ref _skipDuplicates, value);
    }

    public bool RulesAndMode
    {
        get => _rulesAndMode;
        set
        {
            if (SetProperty(ref _rulesAndMode, value))
            {
                if (value) RulesOrMode = false;
                RefreshPreviewDeferred();
            }
        }
    }

    public bool RulesOrMode
    {
        get => _rulesOrMode;
        set
        {
            if (SetProperty(ref _rulesOrMode, value))
            {
                if (value) RulesAndMode = false;
                RefreshPreviewDeferred();
            }
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

    public string RulesActiveText => ApplyRules && Rules.Count > 0 ? "RULES ON" : "";
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

    public bool CanUndo => _organizer.LastUndoableOperation != null;

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
        get => _selectedDateGranularity;
        set
        {
            if (SetProperty(ref _selectedDateGranularity, value))
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
    public ObservableCollection<FilterRuleViewModel> Rules { get; } = [];
    public ObservableCollection<OrganizeOperation> HistoryItems { get; } = [];

    public ICommand AddFolderCommand { get; }
    public ICommand SelectDestinationCommand { get; }
    public AsyncRelayCommand OrganizeCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand SetMethodCommand { get; }
    public ICommand SelectRecentFolderCommand { get; }
    public ICommand RemoveRecentFolderCommand { get; }
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

        AddFolderCommand = new RelayCommand(_ => SelectSourceFolder());
        SelectDestinationCommand = new RelayCommand(_ => SelectDestinationFolder());
        OrganizeCommand = new AsyncRelayCommand(_ => OrganizeAsync(), _ => CanOrganize());
        UndoCommand = new AsyncRelayCommand(_ => UndoLastAsync(), _ => !IsOrganizing && CanUndo);
        UndoOperationCommand = new AsyncRelayCommand(
            p => UndoOperationAsync(p as OrganizeOperation),
            p => !IsOrganizing && p is OrganizeOperation op && _organizer.CanUndoOperation(op));
        AddRuleCommand = new RelayCommand(_ => AddRule());
        SetMethodCommand = new RelayCommand(p =>
        {
            if (p is OrganizeMethod m)
                SelectedMethod = m;
        });
        SelectRecentFolderCommand = new RelayCommand(p =>
        {
            if (p is RecentFolder rf)
            {
                AddCurrentToRecent();
                RecentFolders.Remove(rf);
                SourceFolder = rf.FullPath;
            }
        });
        RemoveRecentFolderCommand = new RelayCommand(p =>
        {
            if (p is RecentFolder rf)
                RecentFolders.Remove(rf);
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

    // INTERIM (Task 2): the VM still holds one global set of options. They are mapped to/from the
    // profile of the current folder; other recent folders keep their stored profiles. Task 3 replaces this.
    public void SaveSettings()
    {
        var existing = SettingsService.Load();
        var settings = new AppSettings
        {
            IsDarkTheme = IsDarkTheme,
            SelectedViewMode = SelectedViewMode,
            StartWithWindows = existing.StartWithWindows,
            PauseAll = existing.PauseAll,
            TrayHintShown = existing.TrayHintShown
        };

        // Current folder first (so it appears in Recent on next launch), then the other recents.
        if (!string.IsNullOrEmpty(SourceFolder))
        {
            var profile = existing.FindProfile(SourceFolder) ?? FolderProfile.CreateDefault(SourceFolder);
            profile.Name = CurrentFolderName;
            profile.FileCount = CurrentFolderFileCount;
            profile.DestinationFolder = DestinationFolder;
            profile.IncludeSubfolders = IncludeSubfolders;
            profile.CopyMode = CopyMode;
            profile.ApplyFilters = ApplyFilters;
            profile.SkipDuplicates = SkipDuplicates;
            profile.Method = SelectedMethod;
            profile.Template = DestinationSubfolderPattern ?? "";
            profile.DateGranularity = SelectedDateGranularity;
            settings.Folders.Add(profile);
        }

        foreach (var rf in RecentFolders)
        {
            if (settings.FindProfile(rf.FullPath) != null) continue;
            var profile = existing.FindProfile(rf.FullPath) ?? FolderProfile.CreateDefault(rf.FullPath);
            profile.Name = rf.Name;
            profile.FileCount = rf.FileCount;
            settings.Folders.Add(profile);
        }

        // Keep max 10
        if (settings.Folders.Count > 10)
            settings.Folders = settings.Folders.Take(10).ToList();

        SettingsService.Save(settings);
    }

    private void LoadSettings()
    {
        var settings = SettingsService.Load();
        var current = settings.Folders.FirstOrDefault() ?? FolderProfile.CreateDefault("");

        DestinationFolder = current.DestinationFolder;
        _isDarkTheme = settings.IsDarkTheme;
        _includeSubfolders = current.IncludeSubfolders;
        _copyMode = current.CopyMode;
        _moveMode = !current.CopyMode;
        _applyFilters = current.ApplyFilters;
        _skipDuplicates = current.SkipDuplicates;
        _selectedMethod = current.Method;
        _selectedViewMode = settings.SelectedViewMode;
        _destinationSubfolderPattern = current.Template;
        _selectedDateGranularity = current.DateGranularity;

        foreach (var profile in settings.Folders)
        {
            RecentFolders.Add(new RecentFolder
            {
                FullPath = profile.FullPath,
                Name = profile.Name,
                FileCount = profile.FileCount
            });
        }
    }

    private void LoadCategories()
    {
        var defaultCategories = FileCategoryService.GetDefaultCategories();
        for (int i = 0; i < defaultCategories.Count; i++)
        {
            var vm = new CategoryViewModel(defaultCategories[i]);
            vm.PropertyChanged += (_, _) => RefreshPreviewDeferred();
            Categories.Add(vm);
        }
    }

    private void AddRule()
    {
        var rule = new FilterRuleViewModel();
        rule.RemoveRequested += () =>
        {
            Rules.Remove(rule);
            RefreshPreviewDeferred();
        };
        rule.PropertyChanged += (_, _) =>
        {
            if (ApplyRules) RefreshPreviewDeferred();
        };
        Rules.Add(rule);
    }

    private void SelectSourceFolder()
    {
        var folder = _dialogs.PickFolder("Select Source Folder");
        if (folder != null)
        {
            AddCurrentToRecent();
            SourceFolder = folder;
        }
    }

    private void AddCurrentToRecent()
    {
        if (string.IsNullOrEmpty(SourceFolder)) return;

        // Don't add duplicates
        var existing = RecentFolders.FirstOrDefault(r => r.FullPath == SourceFolder);
        if (existing != null)
            RecentFolders.Remove(existing);

        RecentFolders.Insert(0, new RecentFolder
        {
            FullPath = SourceFolder,
            Name = CurrentFolderName,
            FileCount = CurrentFolderFileCount
        });

        // Keep max 10 recent folders
        while (RecentFolders.Count > 10)
            RecentFolders.RemoveAt(RecentFolders.Count - 1);
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

    /// <summary>Immutable copy of the filter settings, safe to use from a background thread.</summary>
    private sealed record FilterState(
        bool ApplyFilters, List<FileCategory> Categories, HashSet<string> SelectedCategoryNames,
        bool ApplyRules, bool RulesAndMode, List<FilterRuleSnapshot> Rules);

    private FilterState CaptureFilterState() => new(
        ApplyFilters,
        Categories.Select(c => c.Category).ToList(),
        Categories.Where(c => c.IsSelected).Select(c => c.Name).ToHashSet(),
        ApplyRules,
        RulesAndMode,
        Rules.Select(r => new FilterRuleSnapshot(r.Field, r.Operator, r.Value)).ToList());

    private static List<FileItem> FilterFiles(IEnumerable<FileItem> files, FilterState filter)
    {
        if (filter.ApplyFilters)
        {
            files = files.Where(f =>
                filter.SelectedCategoryNames.Contains(FileCategoryService.GetCategoryName(f.Extension, filter.Categories)));
        }

        if (filter.ApplyRules && filter.Rules.Count > 0)
        {
            files = filter.RulesAndMode
                ? files.Where(f => filter.Rules.All(r => r.Matches(f)))
                : files.Where(f => filter.Rules.Any(r => r.Matches(f)));
        }

        return files.ToList();
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
        var filter = CaptureFilterState();
        var activeCategories = filter.Categories;
        var selectedCategoryNames = filter.SelectedCategoryNames;
        var applyFilters = filter.ApplyFilters;
        var template = string.IsNullOrWhiteSpace(DestinationSubfolderPattern) ? null : DestinationSubfolderPattern;
        var dateFormat = GetDateFormat();
        var method = SelectedMethod;
        var destDisplay = DestinationDisplay;
        Func<ViewFileItem, object?> thumbnailProvider = RequestThumbnail;

        // Heavy work on background thread
        var result = await Task.Run(() =>
        {
            var filteredFiles = FilterFiles(files, filter);

            var preview = _organizer.GeneratePreview(filteredFiles, method, activeCategories, template, dateFormat);

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
                    bool isSelected = !applyFilters || selectedCategoryNames.Contains(kv.Key);
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
            var filter = CaptureFilterState();
            var filteredFiles = FilterFiles(_scannedFiles.ToList(), filter);
            var activeCategories = filter.Categories;
            var progressReporter = new Progress<int>(p => Progress = p);
            var template = string.IsNullOrWhiteSpace(DestinationSubfolderPattern) ? null : DestinationSubfolderPattern;
            var dateFormat = GetDateFormat();

            var result = await _organizer.OrganizeAsync(
                filteredFiles, SourceFolder, DestinationFolder, SelectedMethod,
                CopyMode, activeCategories, progressReporter, template, dateFormat,
                SkipDuplicates);

            var action = CopyMode ? "copied" : "moved";
            var movedCount = result.Moves.Count(m => !m.WasDuplicate);
            var dupeInfo = result.SkippedDuplicates > 0
                ? CopyMode
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

    private async Task UndoLastAsync()
    {
        var lastUndoable = _organizer.LastUndoableOperation;
        if (lastUndoable != null)
            await UndoOperationAsync(lastUndoable);
    }

    private async Task UndoOperationAsync(OrganizeOperation? operation)
    {
        if (operation == null || !_organizer.CanUndoOperation(operation)) return;

        IsOrganizing = true;
        StatusText = "Undoing operation...";
        Progress = 0;

        try
        {
            var progressReporter = new Progress<int>(p => Progress = p);
            var result = await _organizer.UndoOperationAsync(operation, progressReporter);
            PinStatus(result.Failed == 0
                ? $"Undo complete! {result.Restored} files restored"
                : $"Undo partially complete: {result.Restored} files restored, {result.Failed} could not be restored");
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
        }
    }

    private string GetDateFormat() => SelectedDateGranularity switch
    {
        DateGranularity.Year => "yyyy",
        DateGranularity.Month => "yyyy-MM",
        DateGranularity.Day => "yyyy-MM-dd",
        _ => "yyyy-MM"
    };

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
        ObservableCollection<PreviewItem> preview, List<FileCategory> categories,
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
        List<FileCategory> categories, Func<ViewFileItem, object?> thumbnailProvider)
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
