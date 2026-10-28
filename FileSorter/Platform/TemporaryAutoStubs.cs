using FileSorter.Abstractions;

namespace FileSorter.Platform;

// TEMPORARY (Task 10): placeholders until Task 11 adds the real tray notifications and the
// HKCU\...\Run autostart entry. Replace both classes there and delete this file.

/// <summary>TEMPORARY until Task 11: notifications are not shown yet.</summary>
public sealed class NoOpNotificationService : INotificationService
{
    public void ShowAutoOrganized(string title, string message, Action onUndo) { }
}

/// <summary>TEMPORARY until Task 11: remembers the choice in memory only (no registry entry).</summary>
public sealed class NoOpAutostartService : IAutostartService
{
    public bool IsEnabled { get; private set; }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;
}
