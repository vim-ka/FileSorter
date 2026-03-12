using System.Windows;
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

        var vm = new MainViewModel();
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

    public static void SwitchTheme(bool isDark, MainViewModel vm)
    {
        ApplyTheme(isDark);

        var oldWindow = Current.MainWindow;
        var newWindow = new MainWindow { DataContext = vm };
        Current.MainWindow = newWindow;
        newWindow.Show();
        oldWindow?.Close();
    }
}
