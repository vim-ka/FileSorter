using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileSorter.Abstractions;

namespace FileSorter.Platform;

/// <summary>
/// Loads shell thumbnails on demand on a dedicated STA thread (some thumbnail handlers only
/// work in STA) and keeps a bounded LRU cache keyed by path + last write time.
/// Requests are served newest-first so the items currently on screen load before ones scrolled past.
/// </summary>
public sealed class ThumbnailLoader : IThumbnailProvider
{
    private const int CacheCapacity = 2000;

    private readonly record struct Request(string Path, DateTime DateModified, int Size,
        Action<object> OnLoaded, int Generation);

    private readonly Dispatcher _dispatcher;
    private readonly BlockingCollection<Request> _queue = new(new ConcurrentStack<Request>());
    private readonly Dictionary<string, LinkedListNode<(string Key, BitmapSource Image)>> _cache = [];
    private readonly LinkedList<(string Key, BitmapSource Image)> _lru = new();
    private readonly object _cacheLock = new();
    private int _generation;

    public ThumbnailLoader(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        var worker = new Thread(Run) { IsBackground = true, Name = "ThumbnailLoader" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    /// <summary>Drops all pending requests (cache is kept).</summary>
    public void CancelPending() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// Returns the cached thumbnail, or queues loading and returns null; onLoaded is then
    /// invoked on the UI thread with background priority.
    /// </summary>
    public object? GetOrRequest(string path, DateTime dateModified, int size, Action<object> onLoaded)
    {
        if (string.IsNullOrEmpty(path)) return null;

        var cached = TryGetCached(MakeKey(path, dateModified, size));
        if (cached != null) return cached;

        _queue.Add(new Request(path, dateModified, size, onLoaded, Volatile.Read(ref _generation)));
        return null;
    }

    private void Run()
    {
        foreach (var request in _queue.GetConsumingEnumerable())
        {
            if (request.Generation != Volatile.Read(ref _generation)) continue;

            var key = MakeKey(request.Path, request.DateModified, request.Size);
            var thumb = TryGetCached(key);
            if (thumb == null)
            {
                try
                {
                    thumb = ThumbnailService.GetThumbnail(request.Path, request.Size);
                }
                catch
                {
                    thumb = null;
                }
                if (thumb == null) continue;
                AddToCache(key, thumb);
            }

            if (request.Generation != Volatile.Read(ref _generation)) continue;
            _dispatcher.InvokeAsync(() => request.OnLoaded(thumb), DispatcherPriority.Background);
        }
    }

    private static string MakeKey(string path, DateTime dateModified, int size) =>
        $"{path}|{dateModified.Ticks}|{size}";

    private BitmapSource? TryGetCached(string key)
    {
        lock (_cacheLock)
        {
            if (!_cache.TryGetValue(key, out var node)) return null;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return node.Value.Image;
        }
    }

    private void AddToCache(string key, BitmapSource image)
    {
        lock (_cacheLock)
        {
            if (_cache.ContainsKey(key)) return;
            _cache[key] = _lru.AddFirst((key, image));
            while (_cache.Count > CacheCapacity && _lru.Last is { } oldest)
            {
                _lru.RemoveLast();
                _cache.Remove(oldest.Value.Key);
            }
        }
    }
}
