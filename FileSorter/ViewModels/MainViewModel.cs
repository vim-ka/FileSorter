using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FileSorter.Models;
using FileSorter.Services;
using Microsoft.Win32;

namespace FileSorter.ViewModels;

public class MainViewModel : ObservableObject
{
    private readonly FileOrganizerService _organizer = new();
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

    // Colors for categories
    private static readonly Color[] CategoryColors =
    [
        Color.FromRgb(100, 149, 237),  // Application - cornflower blue
        Color.FromRgb(186, 130, 230),  // Archive - purple
        Color.FromRgb(240, 98, 98),    // Documents - red/pink
        Color.FromRgb(245, 158, 115),  // Movies - salmon
        Color.FromRgb(255, 183, 77),   // Music - orange
        Color.FromRgb(220, 220, 90),   // Photos - yellow
        Color.FromRgb(102, 187, 106),  // Text/Code - green
        Color.FromRgb(120, 180, 220),  // Executables - light blue
        Color.FromRgb(158, 158, 158),  // Fonts - gray
        Color.FromRgb(130, 130, 150),  // Other - dim
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
        _ => $"{CategoryStatsItems.Count} Groups"
    };

    public bool CanUndo => _organizer.CanUndo;

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

    public ObservableCollection<CategoryViewModel> Categories { get; } = [];
    public ObservableCollection<PreviewItem> PreviewItems { get; } = [];
    public ObservableCollection<CategoryStats> CategoryStatsItems { get; } = [];
    public ObservableCollection<FilterRuleViewModel> Rules { get; } = [];
    public ObservableCollection<OrganizeOperation> HistoryItems { get; } = [];

    public ICommand AddFolderCommand { get; }
    public ICommand SelectDestinationCommand { get; }
    public ICommand OrganizeCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand SetMethodCommand { get; }
    public ICommand SelectRecentFolderCommand { get; }
    public ICommand RemoveRecentFolderCommand { get; }
    public ICommand ToggleCategoryCommand { get; }
    public ICommand SetDateGranularityCommand { get; }
    public ICommand CloseDatePanelCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ToggleHistoryCommand { get; }
    public ICommand ClearHistoryCommand { get; }

    private List<FileItem> _scannedFiles = [];

    public MainViewModel()
    {
        AddFolderCommand = new RelayCommand(_ => SelectSourceFolder());
        SelectDestinationCommand = new RelayCommand(_ => SelectDestinationFolder());
        OrganizeCommand = new AsyncRelayCommand(_ => OrganizeAsync(), _ => CanOrganize());
        UndoCommand = new AsyncRelayCommand(_ => UndoAsync(), _ => _organizer.CanUndo);
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
        ClearHistoryCommand = new RelayCommand(_ =>
        {
            _organizer.ClearHistory();
            HistoryItems.Clear();
            OnPropertyChanged(nameof(CanUndo));
        });

        LoadCategories();
        LoadSettings();
    }

    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        SaveSettings();
        App.SwitchTheme(IsDarkTheme, this);
    }

    public void SaveSettings()
    {
        var settings = new AppSettings
        {
            DestinationFolder = DestinationFolder,
            IsDarkTheme = IsDarkTheme
        };

        // Add current folder to recent on save (so it appears in Recent on next launch)
        if (!string.IsNullOrEmpty(SourceFolder))
        {
            settings.RecentFolders.Add(new RecentFolderDto
            {
                FullPath = SourceFolder,
                Name = CurrentFolderName,
                FileCount = CurrentFolderFileCount
            });
        }

        foreach (var rf in RecentFolders)
        {
            // Skip duplicates of current folder
            if (rf.FullPath == SourceFolder) continue;
            settings.RecentFolders.Add(new RecentFolderDto
            {
                FullPath = rf.FullPath,
                Name = rf.Name,
                FileCount = rf.FileCount
            });
        }

        // Keep max 10
        if (settings.RecentFolders.Count > 10)
            settings.RecentFolders = settings.RecentFolders.Take(10).ToList();

        SettingsService.Save(settings);
    }

    private void LoadSettings()
    {
        var settings = SettingsService.Load();

        DestinationFolder = settings.DestinationFolder;
        IsDarkTheme = settings.IsDarkTheme;

        foreach (var dto in settings.RecentFolders)
        {
            RecentFolders.Add(new RecentFolder
            {
                FullPath = dto.FullPath,
                Name = dto.Name,
                FileCount = dto.FileCount
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
        var dialog = new OpenFolderDialog { Title = "Select Source Folder" };
        if (dialog.ShowDialog() == true)
        {
            AddCurrentToRecent();
            SourceFolder = dialog.FolderName;
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
        var dialog = new OpenFolderDialog { Title = "Select Destination Folder" };
        if (dialog.ShowDialog() == true)
            DestinationFolder = dialog.FolderName;
    }

    private bool CanOrganize() =>
        !string.IsNullOrEmpty(SourceFolder) && _scannedFiles.Count > 0 && !IsOrganizing;

    private async Task RefreshFilesAsync()
    {
        // Cancel previous scan
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        if (string.IsNullOrEmpty(SourceFolder) || !Directory.Exists(SourceFolder))
        {
            _scannedFiles = [];
            TotalFiles = 0;
            PreviewItems.Clear();
            CategoryStatsItems.Clear();
            StatusText = "Select a folder to start";
            IsScanning = false;
            return;
        }

        _scannedFiles = [];
        TotalFiles = 0;
        IsScanning = true;
        StatusText = "Scanning...";

        var dispatcher = Application.Current.Dispatcher;

        try
        {
            await _organizer.ScanFolderAsync(SourceFolder, IncludeSubfolders, async batch =>
            {
                await dispatcher.InvokeAsync(() =>
                {
                    _scannedFiles.AddRange(batch);
                    TotalFiles = _scannedFiles.Count;
                    CurrentFolderFileCount = _scannedFiles.Count;
                    StatusText = $"Scanning... {_scannedFiles.Count} files found";
                    RefreshPreview();
                });
            }, 1, ct);
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
                StatusText = $"{_scannedFiles.Count} files found";
                IsScanning = false;
                RefreshPreview();
            }
        }
    }

    private List<FileItem> GetFilteredFiles()
    {
        var files = _scannedFiles;

        // Apply category filters
        if (ApplyFilters)
        {
            var activeCategories = Categories
                .Where(c => c.IsSelected)
                .Select(c => c.Name)
                .ToHashSet();

            var categories = Categories.Select(c => c.Category).ToList();
            files = files.Where(f =>
            {
                var cat = FileCategoryService.GetCategoryName(f.Extension, categories);
                return activeCategories.Contains(cat);
            }).ToList();
        }

        // Apply rules
        if (ApplyRules && Rules.Count > 0)
        {
            if (RulesAndMode)
                files = files.Where(f => Rules.All(r => r.Matches(f))).ToList();
            else
                files = files.Where(f => Rules.Any(r => r.Matches(f))).ToList();
        }

        return files;
    }

    private int _refreshVersion;
    private Task? _refreshTask;

    private void RefreshPreviewDeferred()
    {
        RefreshPreview();
    }

    private void RefreshPreview()
    {
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
            PreviewItems.Clear();
            CategoryStatsItems.Clear();
            return;
        }

        // Snapshot on UI thread to avoid race with scan callback
        var files = new List<FileItem>(_scannedFiles);
        var activeCategories = Categories.Select(c => c.Category).ToList();
        var selectedCategoryNames = Categories.Where(c => c.IsSelected).Select(c => c.Name).ToHashSet();
        var template = string.IsNullOrWhiteSpace(DestinationSubfolderPattern) ? null : DestinationSubfolderPattern;
        var dateFormat = GetDateFormat();
        var method = SelectedMethod;
        var applyFilters = ApplyFilters;
        var applyRules = ApplyRules;
        var rulesAndMode = RulesAndMode;
        var rulesCopy = Rules.Select(r => new FilterRuleSnapshot(r.Field, r.Operator, r.Value)).ToList();
        var destDisplay = DestinationDisplay;

        // Heavy work on background thread
        var result = await Task.Run(() =>
        {
            // Filter files
            var filteredFiles = files.ToList();
            if (applyFilters)
            {
                filteredFiles = filteredFiles.Where(f =>
                {
                    var cat = FileCategoryService.GetCategoryName(f.Extension, activeCategories);
                    return selectedCategoryNames.Contains(cat);
                }).ToList();
            }

            if (applyRules && rulesCopy.Count > 0)
            {
                filteredFiles = filteredFiles.Where(f =>
                {
                    var results = rulesCopy.Select(r => r.Matches(f));
                    return rulesAndMode ? results.All(x => x) : results.Any(x => x);
                }).ToList();
            }

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

            return (root, stats, filteredFiles.Count);
        });

        // Update UI
        PreviewItems.Clear();
        CategoryStatsItems.Clear();

        RestoreExpandedState(result.root, expandedNames);
        PreviewItems.Add(result.root);

        foreach (var stat in result.stats)
            CategoryStatsItems.Add(stat);

        TotalFiles = result.Count;
        OnPropertyChanged(nameof(TypeCountText));
        OnPropertyChanged(nameof(RulesActiveText));
        StatusText = $"{result.Count} of {_scannedFiles.Count} files will be organized";
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
            var filteredFiles = GetFilteredFiles();
            var activeCategories = Categories.Select(c => c.Category).ToList();
            var progressReporter = new Progress<int>(p => Progress = p);
            var template = string.IsNullOrWhiteSpace(DestinationSubfolderPattern) ? null : DestinationSubfolderPattern;
            var dateFormat = GetDateFormat();

            var result = await _organizer.OrganizeAsync(
                filteredFiles, SourceFolder, DestinationFolder, SelectedMethod,
                CopyMode, activeCategories, progressReporter, template, dateFormat);

            var action = CopyMode ? "copied" : "moved";
            StatusText = $"Done! {result.Moves.Count} files {action} successfully";
            OnPropertyChanged(nameof(CanUndo));
            RefreshHistory();
            await RefreshFilesAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsOrganizing = false;
            Progress = 0;
        }
    }

    private async Task UndoAsync()
    {
        IsOrganizing = true;
        StatusText = "Undoing last operation...";
        Progress = 0;

        try
        {
            var progressReporter = new Progress<int>(p => Progress = p);
            var count = await _organizer.UndoLastOperationAsync(progressReporter);
            StatusText = $"Undo complete! {count} files restored";
            OnPropertyChanged(nameof(CanUndo));
            RefreshHistory();
            await RefreshFilesAsync();
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

    private void RefreshHistory()
    {
        HistoryItems.Clear();
        foreach (var op in _organizer.History.Reverse())
            HistoryItems.Add(op);
    }
}
