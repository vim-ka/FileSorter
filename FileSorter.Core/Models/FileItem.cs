
namespace FileSorter.Models;

public class FileItem
{
    public string FullPath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FullPath);
    public string Extension => Path.GetExtension(FullPath).ToLowerInvariant();
    public long Size { get; set; }
    public DateTime DateModified { get; set; }
    public DateTime DateCreated { get; set; }
    public DateTime? DateTaken { get; set; }
    public bool DateTakenLoaded { get; set; }
    public string TargetFolder { get; set; } = string.Empty;
}
