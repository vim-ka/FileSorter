using System.Drawing;
using System.Windows.Forms;

namespace FileSorter.Platform;

/// <summary>
/// The notification-area icon: "Open", "Pause all"/"Resume", "Exit"; double click opens. Create and
/// use it on the UI thread — its events are raised there.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _icon;
    private readonly ToolStripMenuItem _pauseItem;
    private bool _pausedAll;

    public event Action? OpenRequested;
    public event Action? PauseAllToggled;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _icon = LoadIcon();

        var menu = new ContextMenuStrip();
        var openItem = new ToolStripMenuItem("Open", null, (_, _) => OpenRequested?.Invoke());
        openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold); // the double-click action
        _pauseItem = new ToolStripMenuItem("Pause all", null, (_, _) => PauseAllToggled?.Invoke());
        menu.Items.Add(openItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke()));

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "FileSorter",
            ContextMenuStrip = menu,
            Visible = false
        };
        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenRequested?.Invoke();
        };
    }

    public bool Visible
    {
        get => _notifyIcon.Visible;
        set => _notifyIcon.Visible = value;
    }

    /// <summary>All auto-organize is paused: the menu offers "Resume" instead of "Pause all".</summary>
    public bool PausedAll
    {
        get => _pausedAll;
        set
        {
            _pausedAll = value;
            _pauseItem.Text = value ? "Resume" : "Pause all";
        }
    }

    /// <summary>A balloon next to the icon; shown only while the icon is visible.</summary>
    public void ShowHint(string title, string text)
    {
        if (_notifyIcon.Visible)
            _notifyIcon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        // Hiding first removes the icon at once instead of leaving a ghost until the mouse passes over it
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private static Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
        if (resource == null)
            return (Icon)SystemIcons.Application.Clone();
        using var stream = resource.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }
}
