using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileSorter.Services;

/// <summary>
/// JSON persistence in %LocalAppData%/FileSorter with atomic writes and corrupt-file backup.
/// </summary>
internal static class JsonFileStore
{
    /// <summary>Settable so tests can redirect persistence away from the real profile.</summary>
    public static string DataDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileSorter");

    // Enums are written as strings; numeric values from older files are still accepted.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Returns null if the file doesn't exist. An unreadable file is renamed to *.bak
    /// (instead of being silently overwritten by the next save) and null is returned.
    /// </summary>
    public static T? Load<T>(string fileName) where T : class
    {
        var path = Path.Combine(DataDir, fileName);
        try
        {
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            try { File.Move(path, path + ".bak", overwrite: true); }
            catch { /* best effort */ }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes to a temp file and then replaces the target, so a crash mid-write can't corrupt it.
    /// </summary>
    public static void Save<T>(string fileName, T value)
    {
        try
        {
            WriteText(PathOf(fileName), Serialize(value));
        }
        catch
        {
            // Ignore save errors
        }
    }

    /// <summary>Full path of a data file in the current <see cref="DataDir"/>.</summary>
    public static string PathOf(string fileName) => Path.Combine(DataDir, fileName);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Atomic write (temp file + replace). Never throws; false when it failed, e.g. the target was
    /// briefly open in another process (a reader, an antivirus scan) — the caller may retry.
    /// </summary>
    public static bool WriteText(string path, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
