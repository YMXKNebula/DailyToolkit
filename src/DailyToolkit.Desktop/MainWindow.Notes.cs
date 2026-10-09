using System.Windows.Interop;
using DailyToolkit.Desktop.Notes;

namespace DailyToolkit.Desktop;

public partial class MainWindow
{
    private NotesController? _notes;
    private bool _exitPrepared;
    private void InitializeNotes(HwndSource source)
    {
        _notes = new(_viewModel.Notes, source, () => _viewModel.Gaming.ToggleShortcut);
        NotesSettings.LensShortcut = () => _viewModel.Gaming.ToggleShortcut;
        NotesSettings.ToggleRequested = _notes.Toggle;
        NotesSettings.FocusRequested = _notes.Refocus;
        NotesSettings.ShortcutEditing = suspended => { _shortcuts?.Suspend(suspended); _notes.Suspend(suspended); };
    }
}
