using FileSorter.Models;

namespace FileSorter.ViewModels;

public class CategoryViewModel : ObservableObject
{
    private bool _isSelected = true;

    public FileCategory Category { get; }

    public string Name => Category.Name;
    public string Icon => Category.Icon;
    public string[] Extensions => Category.Extensions;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                Category.IsSelected = value;
        }
    }

    public CategoryViewModel(FileCategory category)
    {
        Category = category;
        _isSelected = category.IsSelected;
    }
}
