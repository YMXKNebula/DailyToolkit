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
    }
}
