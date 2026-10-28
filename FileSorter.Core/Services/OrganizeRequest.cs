using FileSorter.Models;

namespace FileSorter.Services;

/// <summary>
/// Everything one organize run (or preview) needs, copied from a <see cref="FolderProfile"/>
/// so it can be used safely on a background thread.
/// </summary>
public sealed record OrganizeRequest(
    string SourceFolder,
    string? DestinationFolder,
    OrganizeMethod Method,
    string? Template,
    string DateFormat,
    IReadOnlyList<FileCategory> Categories,
    IReadOnlySet<string>? AllowedCategories,
    IReadOnlyList<RoutingRuleSnapshot> Rules,
    UnmatchedFilesMode Unmatched,
    bool CopyMode,
    bool SkipDuplicates)
{
    /// <summary>Where groups are created: the destination folder if set, else the source folder.</summary>
    public string TargetFolder => string.IsNullOrEmpty(DestinationFolder) ? SourceFolder : DestinationFolder;

    /// <summary>Snapshots the profile. Call on the UI thread; the result shares no mutable state with it.</summary>
    public static OrganizeRequest FromProfile(FolderProfile p, IReadOnlyList<FileCategory> categories) => new(
        p.FullPath,
        string.IsNullOrEmpty(p.DestinationFolder) ? null : p.DestinationFolder,
        p.Method,
        string.IsNullOrWhiteSpace(p.Template) ? null : p.Template,
        GetDateFormat(p.DateGranularity),
        categories.ToList(),
        p.ApplyFilters
            ? categories.Select(c => c.Name).Where(n => !p.DisabledCategories.Contains(n)).ToHashSet()
            : null,
        p.Rules.Select(r => r.Snapshot()).OfType<RoutingRuleSnapshot>().ToList(),
        p.Unmatched,
        p.CopyMode,
        p.SkipDuplicates);

    public static string GetDateFormat(DateGranularity granularity) => granularity switch
    {
        DateGranularity.Year => "yyyy",
        DateGranularity.Month => "yyyy-MM",
        DateGranularity.Day => "yyyy-MM-dd",
        _ => "yyyy-MM"
    };
}

/// <summary>One destination folder of a plan and the files going there.</summary>
/// <param name="FolderPath">Absolute folder path.</param>
/// <param name="DisplaySegments">Path relative to the target folder, split into folders;
/// a single full path for folders outside the target.</param>
public sealed record PlannedGroup(string FolderPath, IReadOnlyList<string> DisplaySegments, IReadOnlyList<FileItem> Files);
