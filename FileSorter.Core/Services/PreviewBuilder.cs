using FileSorter.Models;
using FileSorter.ViewModels;

namespace FileSorter.Services;

/// <summary>One folder's contribution to the preview: its scanned files and its own profile snapshot.</summary>
public sealed record PreviewSource(string FolderName, OrganizeRequest Request, IReadOnlyList<FileItem> Files);

public sealed record PreviewResult(
    IReadOnlyList<PreviewItem> Roots,
    IReadOnlyList<CategoryStats> Stats,
    IReadOnlyList<ViewFileGroup> ViewGroups,
    int FileCount,
    int GroupCount);

/// <summary>
/// Builds the preview tree, chart stats and view groups for one or several folders.
/// Pure apart from lazy EXIF loading — call off the UI thread.
/// </summary>
public static class PreviewBuilder
{
    /// <summary>
    /// Most entries the chart and its (non-virtualized) legend get: grouping by name can yield
    /// thousands of groups, which froze the UI for minutes. The largest groups are kept, the rest
    /// summed into one "+N more" entry.
    /// </summary>
    public const int MaxStatsGroups = 12;

    /// <summary>
    /// One source: a single "Destination → X" root; ByType without a template shows every category
    /// (filtered-out ones as unselected). Several: a "{FolderName} → X" root per folder; stats are
    /// top-level group names summed across folders; view group keys are "{FolderName}/{group}".
    /// </summary>
    public static PreviewResult Build(IReadOnlyList<PreviewSource> sources, IReadOnlyList<RgbColor> colors,
        Func<ViewFileItem, object?> thumbnailProvider)
    {
        bool multi = sources.Count > 1;
        var roots = new List<PreviewItem>();
        var viewGroups = new List<ViewFileGroup>();
        // top-level group name → file count, summed across folders (case-insensitive like folder merging)
        var groupCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var groupOrder = new List<string>();
        int total = 0;

        foreach (var source in sources)
        {
            var preview = FileOrganizerService.GeneratePreview(source.Files, source.Request);
            var root = new PreviewItem
            {
                Name = $"{(multi ? source.FolderName : "Destination")} → {DestinationDisplay(source.Request)}",
                IsFolder = true,
                Icon = ""
            };

            foreach (var group in preview)
            {
                root.Children.Add(group);
                int count = CountFiles(group);
                total += count;
                if (!groupCounts.TryAdd(group.Name, count))
                    groupCounts[group.Name] += count;
                else
                    groupOrder.Add(group.Name);

                var files = new List<ViewFileItem>();
                FlattenPreviewGroup(group, files, source.Request.Categories, thumbnailProvider);
                var key = multi ? $"{source.FolderName}/{group.Name}" : group.Name;
                viewGroups.Add(new ViewFileGroup
                {
                    Key = key,
                    HeaderText = $"{key}  ({files.Count})",
                    FileCount = files.Count,
                    Files = files
                });
            }

            roots.Add(root);
        }

        var stats = !multi && sources.Count == 1
                    && sources[0].Request is { Method: OrganizeMethod.ByType, Template: null }
            ? ByTypeStats(sources[0], roots[0], total, colors)
            : GroupStats(groupOrder.Select(n => (n, groupCounts[n])), total, colors);

        return new PreviewResult(roots, stats, viewGroups, total, groupOrder.Count);
    }

    private static string DestinationDisplay(OrganizeRequest request) =>
        string.IsNullOrEmpty(request.DestinationFolder)
            ? "Folder"
            : Path.GetFileName(request.DestinationFolder) is { Length: > 0 } name ? name : request.DestinationFolder;

    /// <summary>Every category of the scanned files; categories filtered out show as unselected with no sweep.</summary>
    private static List<CategoryStats> ByTypeStats(PreviewSource source, PreviewItem root, int total,
        IReadOnlyList<RgbColor> colors)
    {
        var categories = source.Request.Categories;
        var allowed = source.Request.AllowedCategories;
        var filtered = new List<FileItem>();
        CollectFiles(root, filtered);

        var allCounts = new Dictionary<string, int>();
        foreach (var file in source.Files)
        {
            var name = FileCategoryService.GetCategoryName(file.Extension, categories);
            allCounts[name] = allCounts.GetValueOrDefault(name) + 1;
        }

        var stats = new List<CategoryStats>();
        double angle = 0;
        int colorIndex = 0;
        foreach (var kv in allCounts.OrderBy(k => k.Key))
        {
            bool isSelected = allowed == null || allowed.Contains(kv.Key);
            int groupCount = isSelected
                ? filtered.Count(f => FileCategoryService.GetCategoryName(f.Extension, categories) == kv.Key)
                : 0;
            double pct = isSelected && total > 0 ? (double)groupCount / total : 0;
            double sweep = pct * 360;

            stats.Add(new CategoryStats
            {
                Name = kv.Key, Count = kv.Value, Color = colors[colorIndex % colors.Count],
                Percentage = pct * 100, StartAngle = angle,
                SweepAngle = sweep, IsSelected = isSelected
            });

            if (isSelected) angle += sweep;
            colorIndex++;
        }
        return stats;
    }

    private static List<CategoryStats> GroupStats(IEnumerable<(string Name, int Count)> groups, int total,
        IReadOnlyList<RgbColor> colors)
    {
        var all = groups.ToList();
        var shown = all;
        int restGroups = 0, restCount = 0;
        if (all.Count > MaxStatsGroups)
        {
            var bySize = all.OrderByDescending(g => g.Count).ThenBy(g => g.Name).ToList();
            shown = bySize.Take(MaxStatsGroups - 1).ToList();
            restGroups = bySize.Count - shown.Count;
            restCount = bySize.Skip(shown.Count).Sum(g => g.Count);
        }

        var stats = new List<CategoryStats>();
        double angle = 0;
        int colorIndex = 0;
        var entries = shown.OrderBy(g => g.Name).ToList();
        if (restGroups > 0) entries.Add(($"+{restGroups} more", restCount));
        foreach (var (name, count) in entries)
        {
            double pct = total > 0 ? (double)count / total : 0;
            double sweep = pct * 360;

            stats.Add(new CategoryStats
            {
                Name = name, Count = count, Color = colors[colorIndex % colors.Count],
                Percentage = pct * 100, StartAngle = angle,
                SweepAngle = sweep, IsSelected = true
            });

            angle += sweep;
            colorIndex++;
        }
        return stats;
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

    private static string GetCategoryIcon(string categoryName) => categoryName switch
    {
        "Documents" => "",
        "Images" => "",
        "Videos" => "",
        "Music" => "",
        "Archives" => "",
        "Code" => "",
        "Executables" => "",
        "Fonts" => "",
        _ => ""
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
}
