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

    private static readonly SemaphoreSlim SaveLock = new(1, 1);

    public static Task<List<OrganizeOperation>> LoadAsync() =>
        Task.Run(() => JsonFileStore.Load<List<OrganizeOperation>>(HistoryFile) ?? []);

    /// <summary>
    /// Serializes and writes on a background thread. Callers must not mutate the operations
    /// until the returned task completes.
    /// </summary>
    public static async Task SaveAsync(IReadOnlyList<OrganizeOperation> history)
    {
        var snapshot = history.ToList();

        await SaveLock.WaitAsync();
        try
        {
            await Task.Run(() => JsonFileStore.Save(HistoryFile, snapshot));
        }
        finally
        {
            SaveLock.Release();
        }
    }
}
