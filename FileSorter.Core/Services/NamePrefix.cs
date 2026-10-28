using System.Text.RegularExpressions;

namespace FileSorter.Services;

/// <summary>Derives a grouping prefix from a file name ("IMG_2034.jpg" -> "IMG").</summary>
public static partial class NamePrefix
{
    // Trailing "(3)", digits, or separator characters.
    [GeneratedRegex(@"(\(\d+\)|\d+|[\s._\-]+)$")]
    private static partial Regex TrailingNoise();

    public static string Get(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var regex = TrailingNoise();
        while (true)
        {
            var m = regex.Match(name);
            if (!m.Success || m.Length == 0) break;
            name = name[..m.Index];
        }
        return name.Length == 0 ? "#" : name;
    }
}
