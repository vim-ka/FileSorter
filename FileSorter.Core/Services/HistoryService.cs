using FileSorter.Models;

namespace FileSorter.Services;

public static class HistoryService
{
    private const string HistoryFile = "history.json";

    /// <summary>Oldest operations beyond this limit are dropped from history.</summary>
    public const int MaxOperations = 50;

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
