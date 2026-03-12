namespace FileSorter.Models;

public class FileMove
{
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public bool WasCopied { get; set; }
}

public class OrganizeOperation
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public List<FileMove> Moves { get; set; } = [];
    public string Description { get; set; } = string.Empty;
}
