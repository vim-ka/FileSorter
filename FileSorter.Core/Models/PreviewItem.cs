using System.Collections.ObjectModel;

namespace FileSorter.Models;

public class PreviewItem : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isExpanded = true;

    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public int FileCount { get; set; }
    public FileItem? File { get; set; }
    public ObservableCollection<PreviewItem> Children { get; set; } = [];

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
