using System.Windows.Interop;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.ClipRecall;

namespace DailyToolkit.Desktop;

public partial class MainWindow
{
    private ClipboardHistoryController? _clipRecall;
    private IEnumerable<KeyboardShortcut?> ExistingClipboardShortcuts() =>
        [_viewModel.Gaming.ToggleShortcut, _viewModel.Notes.Preferences.ToggleShortcut, _viewModel.Notes.Preferences.FocusShortcut];
    private IEnumerable<KeyboardShortcut?> ReservedClipboardShortcuts() =>
        [_viewModel.Gaming.ToggleShortcut, _viewModel.Notes.Preferences.ToggleShortcut, _viewModel.Notes.Preferences.FocusShortcut,
            _viewModel.FilePack.IsEnabled ? _viewModel.FilePack.ToggleShortcut : null];
    private void InitializeClipRecall(HwndSource source)
    {
        _clipRecall = new(_viewModel, source, ExistingClipboardShortcuts);
    }
    private void SuspendToolShortcuts(bool suspended)
    {
        if (_applicationRuntime is not null) { _applicationRuntime.SuspendToolShortcuts(suspended); return; }
        if (_shortcuts is not null) UpdateShortcutNotice(_shortcuts.Suspend(suspended));
        _notes?.Suspend(suspended); _clipRecall?.Suspend(suspended);
        _filePack?.Suspend(suspended);
    }
}
