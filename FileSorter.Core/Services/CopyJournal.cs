using FileSorter.Models;

namespace FileSorter.Services;

/// <summary>
/// Persistent record of source files already copied out of a source folder (path + size + mtime),
/// so repeated copy-mode runs don't copy the same, unchanged file again. Thread-safe.
/// </summary>
public class CopyJournal
{
    private const string JournalFile = "copy-journal.json";

    public sealed class Entry
    {
        public string SourceFolder { get; set; } = "";
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public DateTime LastWriteTimeUtc { get; set; }
    }

    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    private readonly object _gate = new();
    private readonly Dictionary<(string Source, string Path), Entry> _entries = [];

    public static async Task<CopyJournal> LoadAsync()
    {
        var journal = new CopyJournal();
        await journal.ReloadAsync();
        return journal;
    }

    /// <summary>Replaces the in-memory entries with those persisted on disk.</summary>
    public async Task ReloadAsync()
    {
        var loaded = await Task.Run(() => JsonFileStore.Load<List<Entry>>(JournalFile) ?? []);
        lock (_gate)
        {
            _entries.Clear();
            foreach (var e in loaded)
            {
                if (string.IsNullOrWhiteSpace(e.SourceFolder) || string.IsNullOrWhiteSpace(e.Path)) continue;
                _entries[Key(e.SourceFolder, e.Path)] = e;
            }
        }
    }

    /// <summary>Serializes and writes on a background thread, under a lock shared with other saves.</summary>
    public async Task SaveAsync()
    {
        List<Entry> snapshot;
        lock (_gate)
            snapshot = _entries.Values.Select(e => new Entry
            {
                SourceFolder = e.SourceFolder,
                Path = e.Path,
                Size = e.Size,
                LastWriteTimeUtc = e.LastWriteTimeUtc
            }).ToList();

        await SaveLock.WaitAsync();
        try
        {
            await Task.Run(() => JsonFileStore.Save(JournalFile, snapshot));
        }
        finally
        {
            SaveLock.Release();
        }
    }

    /// <summary>True when the file was already copied and still has the same size and modification time.</summary>
    public bool Contains(string sourceFolder, FileItem file)
    {
        lock (_gate)
            return _entries.TryGetValue(Key(sourceFolder, file.FullPath), out var e)
                && e.Size == file.Size
                && e.LastWriteTimeUtc == file.DateModified.ToUniversalTime();
    }

    public void Add(string sourceFolder, string path, long size, DateTime lastWriteTimeUtc)
    {
        lock (_gate)
            _entries[Key(sourceFolder, path)] = new Entry
            {
                SourceFolder = PathKey.Normalize(sourceFolder),
                Path = PathKey.Normalize(path),
                Size = size,
                LastWriteTimeUtc = lastWriteTimeUtc
            };
    }

    public void Remove(string sourceFolder, string path)
    {
        lock (_gate)
            _entries.Remove(Key(sourceFolder, path));
    }

    /// <summary>Drops entries of the folder whose file is not among <paramref name="existingRootFiles"/>.</summary>
    public void Prune(string sourceFolder, IEnumerable<string> existingRootFiles)
    {
        var source = PathKey.Normalize(sourceFolder);
        var existing = existingRootFiles.Select(PathKey.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            var stale = _entries.Keys.Where(k => k.Source == source.ToUpperInvariant() && !existing.Contains(k.Path)).ToList();
            foreach (var k in stale) _entries.Remove(k);
        }
    }

    private static (string, string) Key(string sourceFolder, string path) =>
        (PathKey.Normalize(sourceFolder).ToUpperInvariant(), PathKey.Normalize(path).ToUpperInvariant());
}
