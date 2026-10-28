namespace FileSorter.Services;

/// <summary>Case-insensitive path comparison that ignores trailing separators.</summary>
public static class PathKey
{
    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool Equal(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static bool IsUnder(string path, string folder) =>
        Path.GetFullPath(path).StartsWith(Normalize(folder) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
}
