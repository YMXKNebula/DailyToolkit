using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Navigation;
using DailyUSE.Desktop.Presentation;
using Microsoft.Win32;

namespace DailyUSE.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closing;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Loaded += (_, _) => _ = _viewModel.InitializeAsync();
        SizeChanged += (_, _) => _viewModel.SetViewportWidth(ActualWidth);
        Closing += OnClosing;
        Closed += (_, _) => _viewModel.Dispose();
    }

    internal FrameworkElement PreviewContent => RootContent;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) { e.Cancel = true; return; }
        if (_viewModel.CurrentProbeTask.IsCompleted) return;
        e.Cancel = true;
        _closing = true;
        _viewModel.CancelPending();
        await _viewModel.CurrentProbeTask;
        _closing = false;
        Close();
    }

    private async void SaveReport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存电脑信息", FileName = $"DailyUSE-report-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = "JSON 报告 (*.json)|*.json", DefaultExt = ".json", AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, _viewModel.ExportJson(), new UTF8Encoding(false));
            _viewModel.Notice = "报告已保存。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _viewModel.Notice = "没有保存成功，请换一个位置再试。";
        }
    }

    private void CopySummary(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"{_viewModel.SystemName} {_viewModel.SystemDetail}\n" +
                $"处理器：{_viewModel.CpuName}（{_viewModel.CpuDetail}）\n" +
                $"内存：{_viewModel.MemoryTotal}\n显卡：{_viewModel.GraphicsText}\n" +
                $"架构：{_viewModel.Architecture}\n显示：{_viewModel.DisplayDetail}");
            _viewModel.Notice = "摘要已复制。";
        }
        catch (ExternalException) { _viewModel.Notice = "剪贴板正忙，请稍后再试。"; }
    }

    private void OpenRepository(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception) { _viewModel.Notice = "没有打开浏览器。"; }
        e.Handled = true;
    }
}
