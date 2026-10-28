namespace FileSorter.Models;

public class RoutingRule
{
    public bool IsEnabled { get; set; } = true;
    public List<FilterRule> Conditions { get; set; } = [];
    public bool MatchAll { get; set; } = true;
    public string Destination { get; set; } = "";

    /// <summary>Immutable copy for background evaluation; null if the rule is off or has no destination.</summary>
    public RoutingRuleSnapshot? Snapshot() =>
        !IsEnabled || string.IsNullOrWhiteSpace(Destination)
            ? null
            : new RoutingRuleSnapshot(Conditions.Select(c => c.Snapshot()).ToList(), MatchAll, Destination.Trim());
}

/// <summary>Thread-safe routing rule: conditions plus the folder matching files go to.</summary>
public sealed record RoutingRuleSnapshot(
    IReadOnlyList<FilterRuleSnapshot> Conditions, bool MatchAll, string Destination)
{
    /// <summary>A rule without conditions matches every file.</summary>
    public bool Matches(FileItem file)
    {
        if (Conditions.Count == 0) return true;
        return MatchAll ? Conditions.All(c => c.Matches(file)) : Conditions.Any(c => c.Matches(file));
    }

    /// <summary>A fully qualified destination is used as is; otherwise it is a subfolder of the target.</summary>
    public string ResolveFolder(string targetFolder)
    {
        var dest = Destination.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return Path.TrimEndingDirectorySeparator(
            Path.IsPathFullyQualified(dest) ? dest : Path.Combine(targetFolder, dest));
    }
}
