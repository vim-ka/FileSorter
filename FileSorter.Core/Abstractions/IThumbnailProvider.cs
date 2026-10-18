namespace FileSorter.Abstractions;

/// <summary>
/// Supplies platform image objects (e.g. a WPF BitmapSource) for file thumbnails.
/// </summary>
public interface IThumbnailProvider
{
    /// <summary>
    /// Returns a cached thumbnail, or queues loading and returns null; onLoaded is then
    /// invoked on the UI thread.
    /// </summary>
    object? GetOrRequest(string path, DateTime dateModified, int size, Action<object> onLoaded);

    /// <summary>Drops all pending requests.</summary>
    void CancelPending();
}
