using FileSorter.Models;
using FileSorter.Services;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FileSorter.Core.Tests;

/// <summary>Shared helpers: temp folders and redirecting app data away from %LocalAppData%.</summary>
internal static class TestData
{
    static TestData()
    {
        JsonFileStore.DataDir = NewTempDir("appdata");
    }

    /// <summary>Forces the static constructor to run (redirects JsonFileStore.DataDir).</summary>
    public static void RedirectAppData() { }

    public static string NewTempDir(string prefix = "fs")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"FileSorterTests_{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static FileItem Item(string path, DateTime? modified = null, long size = 10) => new()
    {
        FullPath = path,
        Size = size,
        DateModified = modified ?? new DateTime(2024, 5, 1, 12, 0, 0),
        DateCreated = modified ?? new DateTime(2024, 5, 1, 12, 0, 0)
    };

    public static string WriteFile(string dir, string name, string content = "hello")
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public static FileItem ItemFromDisk(string path)
    {
        var info = new FileInfo(path);
        return new FileItem
        {
            FullPath = path,
            Size = info.Length,
            DateModified = info.LastWriteTime,
            DateCreated = info.CreationTime
        };
    }
}
