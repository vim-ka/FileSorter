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

    /// <summary>Operations of one organize run (possibly several folders) share a RunId; Guid.Empty (legacy entries) is a run of its own.</summary>
    public Guid RunId { get; set; }
    public string SourceFolder { get; set; } = string.Empty;
    public bool IsAuto { get; set; }
}

public readonly record struct RunContext(Guid RunId, bool IsAuto)
{
    public static RunContext NewManual() => new(Guid.NewGuid(), false);
    public static RunContext NewAuto() => new(Guid.NewGuid(), true);
}
