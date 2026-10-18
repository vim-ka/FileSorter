namespace FileSorter.Abstractions;

public interface IDialogService
{
    /// <summary>Returns the selected folder path, or null if cancelled.</summary>
    string? PickFolder(string title);

    void ShowError(string title, string message);
}
