using System.Windows;
using System.Windows.Input;
using FileSorter.ViewModels;

namespace FileSorter.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += (_, _) =>
        {
            // Don't save on theme switch (window is recreated, not actually closing)
            if (Application.Current.MainWindow != this) return;
            (DataContext as MainViewModel)?.SaveSettings();
        };
    }

    /// <summary>
    /// Carries over panel widths set via the GridSplitters when the window is recreated on theme switch.
    /// </summary>
    public void CopyLayoutFrom(MainWindow other)
    {
        LeftPanelColumn.Width = other.LeftPanelColumn.Width;
        RightPanelColumn.Width = other.RightPanelColumn.Width;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
        else
        {
            DragMove();
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
