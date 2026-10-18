namespace FileSorter.ViewModels;

public class ViewFileGroup : ObservableObject
{
    private bool _isExpanded = true;

    /// <summary>Stable identity of the group (folder name) — unlike HeaderText it doesn't include the file count.</summary>
    public string Key { get; init; } = string.Empty;
    public string HeaderText { get; init; } = string.Empty;
    public int FileCount { get; init; }
    public List<ViewFileItem> Files { get; init; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
}
