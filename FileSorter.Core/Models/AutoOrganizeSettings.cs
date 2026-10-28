namespace FileSorter.Models;

public enum UnmatchedFilesMode
{
    OrganizeNormally,
    LeaveInPlace
}

public enum ScheduleUnit
{
    Minutes,
    Hours,
    Days
}

public class AutoOrganizeSettings
{
    public bool Enabled { get; set; }
    public bool OnNewFiles { get; set; } = true;
    public bool ScheduleEnabled { get; set; }
    public int ScheduleInterval { get; set; } = 1;
    public ScheduleUnit ScheduleUnit { get; set; } = ScheduleUnit.Hours;
    public bool IsPaused { get; set; }
}
