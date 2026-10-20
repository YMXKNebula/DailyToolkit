using System.Windows.Interop;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.FilePack;

namespace DailyToolkit.Desktop;

public partial class MainWindow
{
    private FilePackController? _filePack;
    private IEnumerable<KeyboardShortcut?> ReservedFilePackShortcuts() =>
        [_viewModel.Gaming.ToggleShortcut, _viewModel.Notes.Preferences.ToggleShortcut, _viewModel.Notes.Preferences.FocusShortcut,
            _viewModel.ClipRecall.IsEnabled ? _viewModel.ClipRecall.Settings.ToggleShortcut : null];
    private void InitializeFilePack(HwndSource source)
    {
        _filePack = new(_viewModel, source, ReservedFilePackShortcuts);
        _filePack.LegacyShortcutReleased += sender =>
        {
            if (ReferenceEquals(sender, _viewModel.Gaming)) RegisterLensHotkey();
            else if (ReferenceEquals(sender, _viewModel.Notes)) _notes?.Suspend(false);
            else if (ReferenceEquals(sender, _viewModel.ClipRecall)) _clipRecall?.Suspend(false);
        };
    }
    internal async Task<bool> LoadFilePackPathsAsync(IReadOnlyList<string> paths)
    {
        if (_filePack is null || _exitRequested || _closing) return false;
        var result = await _filePack.AddShellPathsAsync(paths);
        if (!result) ShowFromTray();
        return result;
    }
}
