namespace FileSorter.Abstractions;

/// <summary>Starting the program together with the operating system (per user).</summary>
public interface IAutostartService
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
