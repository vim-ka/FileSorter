namespace FileSorter.Models;

public class FileMove
{
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public bool WasCopied { get; set; }
    public bool WasDuplicate { get; set; }
    public bool IsUndone { get; set; }

    // Fingerprint of the file written to DestinationPath — undo of a copy only deletes
    // the destination if it still matches, so a newer file at the same path is never lost.
    public long Size { get; set; }
    public DateTime LastWriteTimeUtc { get; set; }
}

public class OrganizeOperation
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public List<FileMove> Moves { get; set; } = [];
    public string Description { get; set; } = string.Empty;
    public int SkippedDuplicates { get; set; }
    public int FailedFiles { get; set; }
    public bool IsUndone { get; set; }
}
