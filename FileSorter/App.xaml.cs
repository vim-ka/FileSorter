using System.ComponentModel;
using System.Windows;
using FileSorter.Platform;
using FileSorter.Services;
using FileSorter.ViewModels;
using FileSorter.Views;

namespace FileSorter;

public partial class App : Application
{
    private const string TrayHintTitle = "FileSorter";
    private const string TrayHintText = "FileSorter keeps running in the tray";

    private MainViewModel? _viewModel;
    private TrayIcon? _tray;
    private ToastNotificationService? _toasts;
    private bool _ownsInstance;

    /// <summary>The old window is being closed by <see cref="SwitchTheme"/>: not a user close.</summary>
    internal static bool IsSwitchingTheme { get; private set; }

    /// <summary><see cref="ExitApplication"/> is running: windows close for real.</summary>
    internal bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A second launch asks the running copy to show its window and quits
        if (!SingleInstance.TryAcquire(() => Dispatcher.InvokeAsync(ShowMainWindow)))
        {
            Shutdown();
            return;
        }
        _ownsInstance = true;

        // The window may be hidden in the tray or not created yet; exits go through ExitApplication
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var settings = SettingsService.Load();
        ApplyTheme(settings.IsDarkTheme);

        var autostart = new RegistryAutostartService();
        autostart.RefreshCommandIfEnabled();
        _toasts = new ToastNotificationService(Dispatcher, ShowMainWindow);

        _viewModel = new MainViewModel(
            new WpfUiDispatcher(Dispatcher),
            new WpfDialogService(),
            new ThumbnailLoader(Dispatcher),
            new WpfThemeService(),
            _toasts,
            autostart);

        _tray = new TrayIcon();
        _tray.OpenRequested += ShowMainWindow;
        _tray.PauseAllToggled += () => _viewModel.TogglePauseAllCommand.Execute(null);
        _tray.ExitRequested += ExitApplication;
        _tray.Visible = _viewModel.KeepRunningInBackground;
        _tray.PausedAll = _viewModel.PauseAll;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Autostart (--tray) or a click on a toast from an earlier run: stay in the tray when there
        // is something to run in the background; otherwise a tray-less start would be invisible
        var background = e.Args.Contains(RegistryAutostartService.TrayArgument, StringComparer.OrdinalIgnoreCase)
                         || ToastNotificationService.WasProcessToastActivated();
        if (!(background && _viewModel.KeepRunningInBackground))
            ShowMainWindow();
    }

    /// <summary>Shows and activates the main window, creating it on the first call after a tray start.</summary>
    public void ShowMainWindow()
    {
        if (IsExiting || _viewModel == null) return;
        if (MainWindow is not MainWindow window)
        {
            window = new MainWindow { DataContext = _viewModel };
            MainWindow = window;
        }

        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>The window was closed into the tray: says so once.</summary>
    internal void OnHiddenToTray()
    {
        if (_viewModel == null || _viewModel.TrayHintShown) return;
        _tray?.ShowHint(TrayHintTitle, TrayHintText);
        _viewModel.TrayHintShown = true;
    }

    /// <summary>Really exits: stops auto-organize and saves settings, removes the tray icon, closes windows.</summary>
    public void ExitApplication()
    {
        if (IsExiting) return;
        IsExiting = true;
        _viewModel?.Shutdown();
        _tray?.Dispose();
        _tray = null;
        Shutdown();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel == null || _tray == null) return;
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.KeepRunningInBackground):
                _tray.Visible = _viewModel.KeepRunningInBackground;
                // No tray icon left to bring a hidden window back: show it
                if (!_viewModel.KeepRunningInBackground && MainWindow is not { IsVisible: true })
                    ShowMainWindow();
                break;
            case nameof(MainViewModel.PauseAll):
                _tray.PausedAll = _viewModel.PauseAll;
                break;
        }
    }

    /// <summary>Logoff/shutdown: windows must close instead of hiding into the tray.</summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel)
            IsExiting = true;
    }

    /// <summary>
    /// Saves settings and stops auto-organize once, when the app really exits — not when the window
    /// is recreated for a theme switch (the view model lives on).
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.Shutdown();
        _tray?.Dispose();
        _tray = null;
        _toasts?.Dispose();
        if (_ownsInstance)
            SingleInstance.Release();
        base.OnExit(e);
    }

    public static void ApplyTheme(bool isDark)
    {
        var themePath = isDark ? "Themes/DarkTheme.xaml" : "Themes/LightTheme.xaml";
        var dict = new ResourceDictionary { Source = new Uri(themePath, UriKind.Relative) };
        Current.Resources.MergedDictionaries.Clear();
        Current.Resources.MergedDictionaries.Add(dict);
    }

    public static void SwitchTheme(bool isDark)
    {
        ApplyTheme(isDark);

        var oldWindow = Current.MainWindow;
        var newWindow = new MainWindow { DataContext = oldWindow?.DataContext };
        if (oldWindow != null)
            CopyPlacement(oldWindow, newWindow);
        if (oldWindow is MainWindow oldMain)
            newWindow.CopyLayoutFrom(oldMain);
        Current.MainWindow = newWindow;
        newWindow.Show();
        IsSwitchingTheme = true;
        try
        {
            oldWindow?.Close();
        }
        finally
        {
            IsSwitchingTheme = false;
        }
    }

    /// <summary>
    /// The recreated window must keep the old one's size, position and maximized state —
    /// otherwise it opens at the default XAML size, centered.
    /// </summary>
    private static void CopyPlacement(Window from, Window to)
    {
        // RestoreBounds holds the normal-state rectangle while maximized/minimized
        var bounds = from.WindowState == WindowState.Normal
            ? new Rect(from.Left, from.Top, from.ActualWidth, from.ActualHeight)
            : from.RestoreBounds;

        to.WindowStartupLocation = WindowStartupLocation.Manual;
        if (!bounds.IsEmpty)
        {
            to.Left = bounds.Left;
            to.Top = bounds.Top;
            to.Width = bounds.Width;
            to.Height = bounds.Height;
        }

        // Set before Show so it maximizes on the monitor given by Left/Top, without a visible resize
        to.WindowState = from.WindowState == WindowState.Minimized ? WindowState.Normal : from.WindowState;
    }
}
