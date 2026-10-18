using FileSorter.Models;

namespace FileSorter.Services;

public class AppSettings
{
    public List<RecentFolderDto> RecentFolders { get; set; } = [];
    public string? DestinationFolder { get; set; }
    public bool IsDarkTheme { get; set; } = true;

    // Checkboxes (Move mode = !CopyMode, OR mode = !RulesAndMode)
    public bool IncludeSubfolders { get; set; } = true;
    public bool CopyMode { get; set; }
    public bool ApplyFilters { get; set; } = true;
    public bool ApplyRules { get; set; }
    public bool RulesAndMode { get; set; }
    public bool SkipDuplicates { get; set; }

    // Organize method & view mode
    public OrganizeMethod SelectedMethod { get; set; } = OrganizeMethod.ByType;
    public FileViewMode SelectedViewMode { get; set; } = FileViewMode.Tree;

    // Template pattern
    public string? DestinationSubfolderPattern { get; set; }
    public DateGranularity SelectedDateGranularity { get; set; } = DateGranularity.Month;
}

public class RecentFolderDto
{
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int FileCount { get; set; }
}

public static class SettingsService
{
    private const string SettingsFile = "settings.json";

    public static AppSettings Load() => JsonFileStore.Load<AppSettings>(SettingsFile) ?? new AppSettings();

    public static void Save(AppSettings settings) => JsonFileStore.Save(SettingsFile, settings);
}
