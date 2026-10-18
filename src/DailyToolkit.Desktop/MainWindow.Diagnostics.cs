using System.IO;
using System.Windows;
using DailyToolkit.Desktop.Runtime;
using Microsoft.Win32;

namespace DailyToolkit.Desktop;

public partial class MainWindow
{
    private bool _exportingDiagnostics;

    private DiagnosticModuleState[] DiagnosticModules() =>
    [
        new(DiagnosticModule.ScreenLens, _viewModel.Gaming.IsEnabled),
        new(DiagnosticModule.FloatingNotes, _viewModel.Notes.IsEnabled),
        new(DiagnosticModule.ClipRecall, _viewModel.ClipRecall.IsEnabled),
        new(DiagnosticModule.FilePack, _viewModel.FilePack.IsEnabled)
    ];

    private async void ExportDiagnostics(object sender, RoutedEventArgs e)
    {
        if (_exportingDiagnostics) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出诊断报告", FileName = "DailyToolkit-diagnostics.json",
            Filter = "诊断报告 (*.json)|*.json", DefaultExt = ".json", AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        _exportingDiagnostics = true;
        ExportDiagnosticsButton.IsEnabled = false;
        try
        {
            await DiagnosticReportService.Shared.ExportAsync(dialog.FileName, DiagnosticModules());
            _viewModel.Notice = "诊断报告已保存。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            DiagnosticReportService.Shared.RecordError(DiagnosticModule.Diagnostics, exception);
            _viewModel.Notice = "未能保存诊断报告，请选择其他位置。";
        }
        finally
        {
            _exportingDiagnostics = false;
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private void ClearDiagnostics(object sender, RoutedEventArgs e)
    {
        DiagnosticReportService.Shared.ClearRecentErrors();
        _viewModel.Notice = "诊断记录已清除。";
    }
}
