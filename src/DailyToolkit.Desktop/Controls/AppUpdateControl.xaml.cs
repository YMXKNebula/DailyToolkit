using System.Windows;
using System.Windows.Controls;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Controls;

public partial class AppUpdateControl : UserControl
{
    private AppUpdateViewModel? _updates;

    public AppUpdateControl()
    {
        InitializeComponent();
        Loaded += (_, _) => { _updates ??= new(); DataContext = _updates; };
        Unloaded += (_, _) => { _updates?.Dispose(); _updates = null; DataContext = null; };
    }

    private async void Update(object sender, RoutedEventArgs e)
    { if (_updates is not null) await _updates.RunAsync(Window.GetWindow(this)); }
    private void Cancel(object sender, RoutedEventArgs e) => _updates?.Cancel();
    private void OpenDownload(object sender, RoutedEventArgs e) => _updates?.OpenDownload();
}
