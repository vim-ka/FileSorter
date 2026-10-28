namespace FileSorter.Models;

public class FolderProfile
{
    public string FullPath { get; set; } = "";
    public string Name { get; set; } = "";
    public int FileCount { get; set; }
    public OrganizeMethod Method { get; set; } = OrganizeMethod.ByType;
    public string Template { get; set; } = "";
    public DateGranularity DateGranularity { get; set; } = DateGranularity.Month;
    public bool ApplyFilters { get; set; } = true;
    public List<string> DisabledCategories { get; set; } = [];
    public string? DestinationFolder { get; set; }
    public bool CopyMode { get; set; }
    public bool IncludeSubfolders { get; set; } = true;
    public bool SkipDuplicates { get; set; }
    public List<RoutingRule> Rules { get; set; } = [];
    public UnmatchedFilesMode Unmatched { get; set; } = UnmatchedFilesMode.OrganizeNormally;
    public AutoOrganizeSettings Auto { get; set; } = new();

    public static FolderProfile CreateDefault(string fullPath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(fullPath);
        var name = Path.GetFileName(trimmed);
        return new FolderProfile
        {
            FullPath = fullPath,
            Name = string.IsNullOrEmpty(name) ? fullPath : name
        };
    }
}
