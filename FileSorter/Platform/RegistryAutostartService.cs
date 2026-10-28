using System.IO;
using System.Security;
using FileSorter.Abstractions;
using Microsoft.Win32;

namespace FileSorter.Platform;

/// <summary>
/// Autostart via the per-user Run key: value <c>FileSorter</c> = <c>"&lt;exe&gt;" --tray</c>. The
/// registry is the source of truth; a failed write leaves the state unchanged (never throws).
/// </summary>
public sealed class RegistryAutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string TrayArgument = "--tray";

    private readonly string _valueName;

    /// <param name="valueName">The Run value; only a manual check uses another name than the default.</param>
    public RegistryAutostartService(string valueName = "FileSorter")
    {
        _valueName = valueName;
    }

    /// <summary>The command line the Run entry should hold for this executable.</summary>
    public static string Command => $"\"{Environment.ProcessPath}\" {TrayArgument}";

    public bool IsEnabled => ReadValue() is { Length: > 0 };

    public void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
                key.SetValue(_valueName, Command, RegistryValueKind.String);
            else
                key.DeleteValue(_valueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // The caller reads IsEnabled back and shows the actual state
        }
    }

    /// <summary>
    /// If autostart is on but the entry points elsewhere (the program was moved or updated to another
    /// folder), rewrites it for this executable.
    /// </summary>
    public void RefreshCommandIfEnabled()
    {
        var current = ReadValue();
        if (current is { Length: > 0 } && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
            SetEnabled(true);
    }

    private string? ReadValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(_valueName) as string;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
