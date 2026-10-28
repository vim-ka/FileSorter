namespace FileSorter.Abstractions;

/// <summary>Shows desktop notifications (e.g. a tray balloon or a toast).</summary>
public interface INotificationService
{
    /// <summary>
    /// Tells the user an automatic run organized files; <paramref name="onUndo"/> is offered as an
    /// "Undo" action and may be invoked from any thread.
    /// </summary>
    void ShowAutoOrganized(string title, string message, Action onUndo);
}
