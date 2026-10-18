using System.Windows;
using FileSorter.Abstractions;
using Microsoft.Win32;

namespace FileSorter.Platform;

public sealed class WpfDialogService : IDialogService
{
    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public void ShowError(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
