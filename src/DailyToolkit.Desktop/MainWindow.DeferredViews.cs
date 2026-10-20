using System.Windows;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.FilePack;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop;

public partial class MainWindow
{
    private NotesSettingsView? _notesSettingsView;
    private ClipboardSettingsView? _clipRecallSettingsView;
    private FilePackSettingsView? _filePackSettingsView;
    private bool _deferredRuntimeBindingsEnabled;
    private Task<bool[]> _deferredAutomationCleanup = Task.FromResult(Array.Empty<bool>());

    internal int CreatedSettingsViewCount => (_notesSettingsView is null ? 0 : 1) +
        (_clipRecallSettingsView is null ? 0 : 1) + (_filePackSettingsView is null ? 0 : 1);
    internal bool IsDeferredAutomationCleanupComplete => _deferredAutomationCleanup.IsCompletedSuccessfully &&
        _deferredAutomationCleanup.Result.All(success => success);
    internal Task<bool[]> DeferredAutomationCleanup => _deferredAutomationCleanup;

    internal bool TryReleaseDeferredViewsForIdle()
    {
        Dispatcher.VerifyAccess();
        if (IsVisible || CreatedSettingsViewCount == 0 || !_deferredAutomationCleanup.IsCompleted || !CanUnloadUi) return false;
        foreach (var (id, values) in CaptureUiScopes(includeMain: false))
            _pendingUiScopes[id] = values;
        AttachUiStateHooks();
        var cleanup = new List<Task<bool>>(3);
        Release(NotesSettingsHost, _notesSettingsView);
        Release(ClipRecallSettingsHost, _clipRecallSettingsView);
        Release(FilePackSettingsHost, _filePackSettingsView);
        _notesSettingsView = null;
        _clipRecallSettingsView = null;
        _filePackSettingsView = null;
        _deferredAutomationCleanup = Task.WhenAll(cleanup);
        return true;

        void Release(System.Windows.Controls.ContentControl host, FrameworkElement? view)
        {
            if (view is null) return;
            var automation = RetiredAutomationProviders.Capture(view);
            UnregisterName(view.Name);
            host.Content = null;
            // DataContextChanged/Unloaded detach the existing model/static
            // subscriptions. The model keeps drafts; no Save or UpdateSource.
            view.DataContext = null;
            cleanup.Add(automation.DisconnectWhenIdle(Dispatcher));
        }
    }

    private void EnsureSelectedSettingsView()
    {
        // Visibility/layout events can arrive before the constructor has assigned
        // the view model. Hidden startup and minimized navigation keep empty hosts.
        if (_viewModel is null || !IsVisible || WindowState == WindowState.Minimized || _closing) return;
        if (_viewModel.ShowFloatingNotes && _notesSettingsView is null)
        {
            var view = new NotesSettingsView { Name = "NotesSettings", DataContext = _viewModel.Notes };
            if (_deferredRuntimeBindingsEnabled)
            {
                // These callbacks read the current controllers/settings when used;
                // creating or initializing a controller never constructs a page.
                view.LensShortcut = () => _viewModel.Gaming.ToggleShortcut;
                view.ClipRecallShortcut = () => _viewModel.ClipRecall.IsEnabled ? _viewModel.ClipRecall.Settings.ToggleShortcut : null;
                view.FilePackShortcut = () => _viewModel.FilePack.IsEnabled ? _viewModel.FilePack.ToggleShortcut : null;
                view.ToggleRequested = () => _notes?.Toggle();
                view.ShortcutEditing = SuspendToolShortcuts;
            }
            _notesSettingsView = view;
            RegisterName(view.Name, view);
            NotesSettingsHost.Content = view;
        }
        else if (_viewModel.ShowClipRecall && _clipRecallSettingsView is null)
        {
            var view = new ClipboardSettingsView { Name = "ClipRecallSettings", DataContext = _viewModel.ClipRecall };
            if (_deferredRuntimeBindingsEnabled)
            {
                view.ReservedShortcuts = ReservedClipboardShortcuts;
                view.ShortcutEditing = SuspendToolShortcuts;
            }
            _clipRecallSettingsView = view;
            RegisterName(view.Name, view);
            ClipRecallSettingsHost.Content = view;
        }
        else if (_viewModel.ShowFilePack && _filePackSettingsView is null)
        {
            var view = new FilePackSettingsView { Name = "FilePackSettings", DataContext = _viewModel.FilePack };
            if (_deferredRuntimeBindingsEnabled)
            {
                view.ReservedShortcuts = ReservedFilePackShortcuts;
                view.ShortcutEditing = SuspendToolShortcuts;
            }
            _filePackSettingsView = view;
            RegisterName(view.Name, view);
            FilePackSettingsHost.Content = view;
        }
        // Visible navigation keeps the current editing session. Hidden idle
        // release retains only values and recreates a page on its next visit.
        QueueUiStateRestore();
    }
}
