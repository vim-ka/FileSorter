using FileSorter.Models;

namespace FileSorter.Services;

/// <summary>
/// Warnings against "ping-pong" between folders: files sent into the root (or below) of another
/// auto-organized folder may be organized again there, and nested auto folders feed each other.
/// </summary>
public static class ProfileConflicts
{
    public static IReadOnlyList<string> Find(FolderProfile profile, IReadOnlyList<FolderProfile> all)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(profile.FullPath)) return warnings;

        var others = all
            .Where(o => o.Auto.Enabled && !string.IsNullOrWhiteSpace(o.FullPath) && !SafeEqual(o.FullPath, profile.FullPath))
            .ToList();
        if (others.Count == 0) return warnings;

        var destination = string.IsNullOrWhiteSpace(profile.DestinationFolder) ? null : profile.DestinationFolder;
        var ruleFolders = profile.Rules
            .Select(r => r.Snapshot())
            .OfType<RoutingRuleSnapshot>()
            .Where(r => Path.IsPathFullyQualified(r.Destination))
            .Select(r => r.ResolveFolder(""))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var other in others)
        {
            if (destination != null && IsSameOrInside(destination, other.FullPath))
                warnings.Add($"Destination folder is inside auto-organized folder \"{other.Name}\" — files sent there may be organized again.");

            foreach (var folder in ruleFolders.Where(f => IsSameOrInside(f, other.FullPath)))
                warnings.Add($"Rule destination \"{folder}\" is inside auto-organized folder \"{other.Name}\" — files sent there may be organized again.");

            if (profile.Auto.Enabled
                && (IsSameOrInside(profile.FullPath, other.FullPath) || IsSameOrInside(other.FullPath, profile.FullPath)))
                warnings.Add($"This folder and auto-organized folder \"{other.Name}\" are nested in each other.");
        }

        return warnings;
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        try
        {
            return PathKey.Equal(path, folder) || PathKey.IsUnder(path, folder);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false; // an invalid path can't collide with anything
        }
    }

    private static bool SafeEqual(string a, string b)
    {
        try { return PathKey.Equal(a, b); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
