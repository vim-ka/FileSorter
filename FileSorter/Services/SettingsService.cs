using System.IO;
using System.Text.Json;

namespace FileSorter.Services;

public class AppSettings
{
    public string? CurrentFolder { get; set; }
    public List<RecentFolderDto> RecentFolders { get; set; } = [];
    public string? DestinationFolder { get; set; }
    public bool IsDarkTheme { get; set; } = true;
}

public class RecentFolderDto
{
    public string FullPath { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int FileCount { get; set; }
}

public static class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileSorter");

    private static readonly string SettingsFile =
        Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsFile))
                return new AppSettings();

            var json = File.ReadAllText(SettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
        }
        catch
        {
            // Ignore save errors
        }
    }
}
