using FileSorter.Models;

namespace FileSorter.Services;

public class AppSettings
{
    public int Version { get; set; } = 2;
    public List<FolderProfile> Folders { get; set; } = [];
    public bool IsDarkTheme { get; set; } = true;
    public FileViewMode SelectedViewMode { get; set; } = FileViewMode.Tree;
    public bool StartWithWindows { get; set; }
    public bool PauseAll { get; set; }
    public bool TrayHintShown { get; set; }

    public FolderProfile? FindProfile(string path) =>
        Folders.FirstOrDefault(f => PathKey.Equal(f.FullPath, path));
}

public class RecentFolderDto
{
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int FileCount { get; set; }
}

/// <summary>Shape of settings.json before per-folder profiles (v1): one global set of options.</summary>
internal sealed class LegacyAppSettings
{
    public List<RecentFolderDto> RecentFolders { get; set; } = [];
    public string? DestinationFolder { get; set; }
    public bool IsDarkTheme { get; set; } = true;
    public bool IncludeSubfolders { get; set; } = true;
    public bool CopyMode { get; set; }
    public bool ApplyFilters { get; set; } = true;
    public bool SkipDuplicates { get; set; }
    public OrganizeMethod SelectedMethod { get; set; } = OrganizeMethod.ByType;
    public FileViewMode SelectedViewMode { get; set; } = FileViewMode.Tree;
    public string? DestinationSubfolderPattern { get; set; }
    public DateGranularity SelectedDateGranularity { get; set; } = DateGranularity.Month;
}

internal sealed class SettingsVersionProbe
{
    public int? Version { get; set; }
}

public static class SettingsService
{
    private const string SettingsFile = "settings.json";

    public static AppSettings Load()
    {
        var probe = JsonFileStore.Load<SettingsVersionProbe>(SettingsFile);
        if (probe == null)
            return new AppSettings();

        AppSettings settings;
        if (probe.Version is >= 2)
        {
            settings = JsonFileStore.Load<AppSettings>(SettingsFile) ?? new AppSettings();
        }
        else
        {
            var legacy = JsonFileStore.Load<LegacyAppSettings>(SettingsFile);
            settings = legacy == null ? new AppSettings() : Migrate(legacy);
        }

        Normalize(settings);
        return settings;
    }

    public static void Save(AppSettings settings) => JsonFileStore.Save(SettingsFile, settings);

    private static AppSettings Migrate(LegacyAppSettings legacy)
    {
        var settings = new AppSettings
        {
            IsDarkTheme = legacy.IsDarkTheme,
            SelectedViewMode = legacy.SelectedViewMode
        };

        foreach (var recent in legacy.RecentFolders)
        {
            if (string.IsNullOrWhiteSpace(recent.FullPath) || settings.FindProfile(recent.FullPath) != null)
                continue;

            var profile = FolderProfile.CreateDefault(recent.FullPath);
            if (!string.IsNullOrEmpty(recent.Name))
                profile.Name = recent.Name;
            profile.FileCount = recent.FileCount;
            profile.Method = legacy.SelectedMethod;
            profile.Template = legacy.DestinationSubfolderPattern ?? "";
            profile.DateGranularity = legacy.SelectedDateGranularity;
            profile.ApplyFilters = legacy.ApplyFilters;
            profile.DestinationFolder = legacy.DestinationFolder;
            profile.CopyMode = legacy.CopyMode;
            profile.IncludeSubfolders = legacy.IncludeSubfolders;
            profile.SkipDuplicates = legacy.SkipDuplicates;
            settings.Folders.Add(profile);
        }

        return settings;
    }

    /// <summary>Replaces undefined enum values (hand-edited or newer files) with defaults.</summary>
    private static void Normalize(AppSettings settings)
    {
        settings.Version = 2;
        settings.SelectedViewMode = ValidOrDefault(settings.SelectedViewMode, FileViewMode.Tree);
        settings.Folders ??= [];

        foreach (var p in settings.Folders)
        {
            p.Method = ValidOrDefault(p.Method, OrganizeMethod.ByType);
            p.DateGranularity = ValidOrDefault(p.DateGranularity, DateGranularity.Month);
            p.Unmatched = ValidOrDefault(p.Unmatched, UnmatchedFilesMode.OrganizeNormally);
            p.Template ??= "";
            p.DisabledCategories ??= [];
            p.Rules ??= [];
            p.Auto ??= new AutoOrganizeSettings();
            p.Auto.ScheduleUnit = ValidOrDefault(p.Auto.ScheduleUnit, ScheduleUnit.Hours);
            foreach (var rule in p.Rules)
            {
                rule.Conditions ??= [];
                foreach (var c in rule.Conditions)
                {
                    c.Field = ValidOrDefault(c.Field, RuleField.Name);
                    c.Operator = ValidOrDefault(c.Operator, RuleOperator.Contains);
                }
            }
        }
    }

    private static T ValidOrDefault<T>(T value, T fallback) where T : struct, Enum =>
        Enum.IsDefined(value) ? value : fallback;
}
