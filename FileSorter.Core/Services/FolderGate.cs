namespace FileSorter.Services;

/// <summary>
/// At most one organize or undo (manual or automatic) per source folder at a time. Thread-safe;
/// <see cref="Changed"/> is raised on the calling thread, outside the lock.
/// </summary>
public class FolderGate
{
    private readonly object _lock = new();
    private readonly HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A folder became busy or free (argument: the folder as passed in).</summary>
    public event Action<string>? Changed;

    public bool TryEnter(string folder)
    {
        lock (_lock)
            if (!_busy.Add(PathKey.Normalize(folder))) return false;
        Changed?.Invoke(folder);
        return true;
    }

    public void Exit(string folder)
    {
        lock (_lock)
            if (!_busy.Remove(PathKey.Normalize(folder))) return;
        Changed?.Invoke(folder);
    }

    public bool IsBusy(string folder)
    {
        lock (_lock)
            return _busy.Contains(PathKey.Normalize(folder));
    }
}
