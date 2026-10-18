using FileSorter.Abstractions;

namespace FileSorter.Platform;

public sealed class WpfThemeService : IThemeService
{
    public void ApplyTheme(bool isDark) => App.SwitchTheme(isDark);
}
