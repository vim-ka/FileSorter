using FileSorter.Models;

namespace FileSorter.Services;

/// <summary>
/// At most one notification per folder per interval: a folder's first run is shown at once,
/// later runs within the interval are collected and shown together by <see cref="Tick"/>
/// (or by the next <see cref="Add"/> after the interval). Not thread-safe — use on the UI thread.
/// </summary>
public class NotificationThrottle(TimeSpan interval)
{
    private sealed class FolderState
    {
        public DateTime LastShownUtc;
        public readonly List<OrganizeOperation> Pending = [];
    }

    private readonly Dictionary<string, FolderState> _folders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Some runs are waiting to be shown — keep calling <see cref="Tick"/>.</summary>
    public bool HasPending => _folders.Values.Any(f => f.Pending.Count > 0);

    public void Add(string folder, OrganizeOperation op, DateTime nowUtc,
        Action<string, IReadOnlyList<OrganizeOperation>> flush)
    {
        if (!_folders.TryGetValue(folder, out var state))
        {
            _folders[folder] = new FolderState { LastShownUtc = nowUtc };
            flush(folder, [op]);
            return;
        }

        state.Pending.Add(op);
        FlushIfDue(folder, state, nowUtc, flush);
    }

    public void Tick(DateTime nowUtc, Action<string, IReadOnlyList<OrganizeOperation>> flush)
    {
        foreach (var (folder, state) in _folders.ToList())
            FlushIfDue(folder, state, nowUtc, flush);
    }

    private void FlushIfDue(string folder, FolderState state, DateTime nowUtc,
        Action<string, IReadOnlyList<OrganizeOperation>> flush)
    {
        if (state.Pending.Count == 0 || nowUtc - state.LastShownUtc < interval) return;
        var ops = state.Pending.ToList();
        state.Pending.Clear();
        state.LastShownUtc = nowUtc;
        flush(folder, ops);
    }
}
