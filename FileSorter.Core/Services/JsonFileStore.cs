using System.Text.Json;
using System.Text.Json.Serialization;

namespace FileSorter.Services;

/// <summary>
/// JSON persistence in %LocalAppData%/FileSorter with atomic writes and corrupt-file backup.
/// </summary>
internal static class JsonFileStore
{
    public static readonly string DataDir =
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
            Directory.CreateDirectory(DataDir);
            var path = Path.Combine(DataDir, fileName);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Ignore save errors
        }
    }
}
