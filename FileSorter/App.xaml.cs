using System.Windows;
using FileSorter.Platform;
using FileSorter.Services;
using FileSorter.ViewModels;
using FileSorter.Views;

namespace FileSorter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settings = SettingsService.Load();
        ApplyTheme(settings.IsDarkTheme);

        var vm = new MainViewModel(
            new WpfUiDispatcher(Dispatcher),
            new WpfDialogService(),
            new ThumbnailLoader(Dispatcher),
            new WpfThemeService());
        var window = new MainWindow { DataContext = vm };
        window.Show();
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
        oldWindow?.Close();
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
