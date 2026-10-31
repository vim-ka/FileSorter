namespace FileSorter.Models;

public class RoutingRule
{
    public bool IsEnabled { get; set; } = true;
    public List<FilterRule> Conditions { get; set; } = [];
    public bool MatchAll { get; set; } = true;
    public string Destination { get; set; } = "";

    /// <summary>
    /// Immutable copy for background evaluation; null if the rule is off, its destination is
    /// invalid (drive-relative like <c>D:Movies</c>, which is neither a subfolder nor a full path),
    /// or any condition can't be evaluated (blank or unparsable value, operator not supported for the
    /// field — see <see cref="FilterRule.IsEvaluable"/>): such a rule must never route every file.
    /// An empty destination means "organize matching files normally" (by the method or template).
    /// A rule without conditions matches every file.
    /// </summary>
    public RoutingRuleSnapshot? Snapshot()
    {
        if (!IsEnabled) return null;
        if (Conditions.Any(c => !c.IsEvaluable)) return null;
        var destination = Destination.Trim();
        if (destination.Length > 0 && !Path.IsPathFullyQualified(destination)
            && Path.IsPathRooted(RoutingRuleSnapshot.TrimLeadingSeparators(destination)))
            return null;
        return new RoutingRuleSnapshot(Conditions.Select(c => c.Snapshot()).ToList(), MatchAll, destination);
    }
}

/// <summary>Thread-safe routing rule: conditions plus the folder matching files go to.</summary>
public sealed record RoutingRuleSnapshot(
    IReadOnlyList<FilterRuleSnapshot> Conditions, bool MatchAll, string Destination)
{
    /// <summary>No folder: matching files are organized by the method/template like unmatched ones.</summary>
    public bool OrganizesNormally => Destination.Length == 0;

    /// <summary>A rule without conditions matches every file.</summary>
    public bool Matches(FileItem file)
    {
        if (Conditions.Count == 0) return true;
        return MatchAll ? Conditions.All(c => c.Matches(file)) : Conditions.Any(c => c.Matches(file));
    }

    /// <summary>
    /// A fully qualified destination is used as is; anything else is a subfolder of the target
    /// (leading separators are ignored, so <c>/Movies</c> never escapes to the drive root).
    /// The result is normalized (<c>.\Movies</c>, <c>Movies\..\Movies</c> → <c>target\Movies</c>);
    /// with an empty target the relative path is returned as is.
    /// </summary>
    public string ResolveFolder(string targetFolder)
    {
        var dest = Destination.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (Path.IsPathFullyQualified(dest))
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dest));

        var relative = TrimLeadingSeparators(dest);
        if (string.IsNullOrWhiteSpace(targetFolder))
            return Path.TrimEndingDirectorySeparator(relative);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(targetFolder, relative)));
    }

    internal static string TrimLeadingSeparators(string path) =>
        path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
