using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop;

public partial class MainWindow
{
    private ApplicationRuntimeHost? _applicationRuntime;
    private bool _releasingForIdle, _uiClosed;

    internal MainWindow(MainViewModel viewModel, ApplicationRuntimeHost runtime) : this(viewModel, enableShortcuts: false)
    {
        _applicationRuntime = runtime;
        _deferredRuntimeBindingsEnabled = true;
        _trackActivity = true;
        _shortcuts = runtime.LensShortcuts;
        _notes = runtime.Notes;
        _clipRecall = runtime.ClipRecall;
        _filePack = runtime.FilePack;
        // Runtime subscriptions belong to the stable host, including hold-mode
        // shortcuts. Rebuilding a window must not reconfigure a held shortcut.
        _viewModel.Gaming.PropertyChanged -= OnGamingStateChanged;
    }

    internal bool IsExitPrepared => _exitPrepared;
    internal bool IsExitInProgress => _exitRequested || _closing;

    internal bool TryReleaseForIdle(out MainWindowUiState state)
    {
        state = null!;
        if (_applicationRuntime is null || IsVisible || _closing || _uiClosed || !CanUnloadUi) return false;
        state = CaptureUiState();
        _releasingForIdle = true;
        try { Close(); }
        finally { if (!_uiClosed) _releasingForIdle = false; }
        return _uiClosed;
    }
}
