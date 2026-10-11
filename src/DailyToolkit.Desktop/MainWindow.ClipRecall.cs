using System.Windows.Interop;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.ClipRecall;

namespace DailyToolkit.Desktop;

public partial class MainWindow
{
    private ClipboardHistoryController? _clipRecall;
    private IEnumerable<KeyboardShortcut?> ReservedClipboardShortcuts() =>
        [_viewModel.Gaming.ToggleShortcut, _viewModel.Notes.Preferences.ToggleShortcut, _viewModel.Notes.Preferences.FocusShortcut];
    private void InitializeClipRecall(HwndSource source)
    {
        _clipRecall = new(_viewModel, source, ReservedClipboardShortcuts);
        _clipRecall.SettingsRequested = () => { ShowFromTray(); _viewModel.Page = "clip-recall"; };
        ClipRecallSettings.ReservedShortcuts = ReservedClipboardShortcuts;
        ClipRecallSettings.ShortcutEditing = SuspendToolShortcuts;
        NotesSettings.ClipRecallShortcut = () => _viewModel.ClipRecall.IsEnabled ? _viewModel.ClipRecall.Settings.ToggleShortcut : null;
    }
    private void SuspendToolShortcuts(bool suspended)
    {
        if (_shortcuts is not null) UpdateShortcutNotice(_shortcuts.Suspend(suspended));
        _notes?.Suspend(suspended); _clipRecall?.Suspend(suspended);
    }
}
