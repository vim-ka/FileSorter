using FileSorter.Models;

namespace FileSorter.Services;

public static class HistoryService
{
    private const string HistoryFile = "history.json";

    /// <summary>Oldest manual operations beyond this limit are dropped from history.</summary>
    public const int MaxManualOperations = 50;

    /// <summary>Oldest automatic operations beyond this limit are dropped; counted separately from manual ones.</summary>
    public const int MaxAutoOperations = 200;

    /// <summary>Keeps the newest manual and the newest automatic operations, preserving chronological order.</summary>
    public static void Trim(List<OrganizeOperation> history)
    {
        var manualExcess = history.Count(o => !o.IsAuto) - MaxManualOperations;
        var autoExcess = history.Count(o => o.IsAuto) - MaxAutoOperations;
        if (manualExcess <= 0 && autoExcess <= 0) return;

        history.RemoveAll(o =>
        {
            if (o.IsAuto) return autoExcess-- > 0;
            return manualExcess-- > 0;
        });
    }

    /// <summary>Serializes writes; a write never replaces the file with an older snapshot than one already written.</summary>
    private static readonly object WriteLock = new();
    private static long _lastQueued;
    private static long _lastWritten;

    public static Task<List<OrganizeOperation>> LoadAsync() =>
        Task.Run(() => JsonFileStore.Load<List<OrganizeOperation>>(HistoryFile) ?? []);

    /// <summary>
    /// Snapshots the list on the calling thread, then serializes and writes on a background thread.
    /// Callers must not mutate the operations until the returned task completes. Saves completing
    /// out of order are harmless: an older snapshot never overwrites a newer one.
    /// </summary>
    public static Task SaveAsync(IReadOnlyList<OrganizeOperation> history)
    {
        var (path, snapshot, seq) = Prepare(history);
        return Task.Run(() => Write(path, snapshot, seq));
    }

    /// <summary>Writes now, on the calling thread (exit). Waits only for a write already in progress.</summary>
    public static void Save(IReadOnlyList<OrganizeOperation> history)
    {
        var (path, snapshot, seq) = Prepare(history);
        Write(path, snapshot, seq);
    }

    /// <summary>A save was requested whose snapshot (or a newer one) hasn't been written yet.</summary>
    public static bool HasUnwrittenSaves => Interlocked.Read(ref _lastWritten) < Interlocked.Read(ref _lastQueued);

    private static (string Path, List<OrganizeOperation> Snapshot, long Seq) Prepare(IReadOnlyList<OrganizeOperation> history) =>
        (JsonFileStore.PathOf(HistoryFile), history.ToList(), Interlocked.Increment(ref _lastQueued));

    private static void Write(string path, List<OrganizeOperation> snapshot, long seq)
    {
        lock (WriteLock)
        {
            if (seq <= Interlocked.Read(ref _lastWritten)) return;
            try { JsonFileStore.WriteText(path, JsonFileStore.Serialize(snapshot)); }
            catch { /* ignore save errors, as before */ }
            Interlocked.Exchange(ref _lastWritten, seq);
        }
    }
}
