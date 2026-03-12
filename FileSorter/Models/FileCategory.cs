namespace FileSorter.Models;

public class FileCategory
{
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public string[] Extensions { get; set; } = [];
    public bool IsSelected { get; set; } = true;
}
