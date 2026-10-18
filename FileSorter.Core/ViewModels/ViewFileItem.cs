namespace FileSorter.ViewModels;

public class ViewFileItem : ObservableObject
{
    private object? _thumbnail;
    private bool _thumbnailRequested;

    public bool IsHeader { get; init; }
    public ViewFileGroup? Group { get; init; }
    public string HeaderText { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public string Icon { get; init; } = string.Empty;
    public string SizeText { get; init; } = string.Empty;
    public string DateText { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public DateTime DateModified { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public bool HasThumbnailSupport { get; init; }

    /// <summary>
    /// Returns a cached thumbnail or queues loading and returns null; the loaded image is
    /// delivered later through the Thumbnail setter.
    /// </summary>
    public Func<ViewFileItem, object?>? ThumbnailProvider { get; init; }

    /// <summary>
    /// Platform image object (BitmapSource on WPF). Loaded lazily: the first binding read (only happens for realized, i.e. visible,
    /// containers of the virtualized list) requests the thumbnail.
    /// </summary>
    public object? Thumbnail
    {
        get
        {
            if (_thumbnail == null && !_thumbnailRequested && HasThumbnailSupport && ThumbnailProvider != null)
            {
                _thumbnailRequested = true;
                _thumbnail = ThumbnailProvider(this);
            }
            return _thumbnail;
        }
        set
        {
            if (SetProperty(ref _thumbnail, value))
                OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => Thumbnail != null;
}
