using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop.Notes;

internal sealed class NotesController : IDisposable
{
    private readonly NotesViewModel _model;
    private readonly Func<KeyboardShortcut?> _lensShortcut;
    private readonly NotesShortcutController _shortcuts;
    private readonly NotesFocusManager _focus;
    private NotesWindow? _window;
    private Task _operation = Task.CompletedTask;
    private (bool Enabled, KeyboardShortcut? Toggle, KeyboardShortcut? Focus, KeyboardShortcut? Lens)? _registered;
    private bool _stopping, _disposed;
    private int _selectionStart, _selectionLength;
    private double _verticalOffset;
    public Task Ready { get; }
    internal NotesWindow? NoteWindow => _window;
    internal IntPtr RestoreTarget => _focus.Previous;
    internal void RetargetPreviousWindow(IntPtr previous, IntPtr replacement) => _focus.RetargetPreviousWindow(previous, replacement);
    internal Task Pending => _operation;
    internal double SavedVerticalOffset => _verticalOffset;

    public NotesController(NotesViewModel model, WindowMessageSource source, Func<KeyboardShortcut?> lensShortcut,
        INotesForeground? foreground = null)
    {
        _model = model; _lensShortcut = lensShortcut; _shortcuts = new(source);
        _focus = new(foreground ?? new NotesForeground(), () => _window?.Handle ?? IntPtr.Zero);
        _shortcuts.ToggleRequested += Toggle;
        _shortcuts.FocusRequested += ToggleFocus;
        _model.PropertyChanged += Changed;
        Ready = InitializeAsync();
    }
    private async Task InitializeAsync()
    {
        await Task.Yield(); // Assign Ready before a disabled startup queues its release operation.
        await _model.Ready;
        if (_disposed) return;
        Reconfigure();
        if (_model.IsEnabled && !_model.IsVisible && await _model.FlushAsync()) _model.ReleaseInactiveContent();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("" or nameof(NotesViewModel.Preferences))) return;
        Reconfigure();
    }
    public void Reconfigure()
    {
        if (_disposed || _stopping || !_model.IsLoaded) return;
        var preferences = _model.Preferences;
        var next = (preferences.IsEnabled, preferences.ToggleShortcut, preferences.FocusShortcut, _lensShortcut());
        if (_registered == next) return;
        _registered = next;
        var toggle = preferences.ToggleShortcut; var focus = preferences.FocusShortcut;
        if (next.Item4 is not null)
        {
            if (next.Item4.Matches(toggle)) toggle = null;
            if (next.Item4.Matches(focus)) focus = null;
            if (toggle != preferences.ToggleShortcut || focus != preferences.FocusShortcut)
                _model.Notice = "浮笺快捷键与放大镜冲突，请修改浮笺快捷键。";
        }
        var success = _shortcuts.Configure(preferences.IsEnabled ? toggle : null, preferences.IsEnabled ? focus : null);
        if (!success) _model.Notice = _shortcuts.Error;
        else if (_model.Notice.StartsWith("浮笺快捷键注册失败", StringComparison.Ordinal)) _model.Notice = "";
        if (!preferences.IsEnabled) Queue(async () =>
        {
            if (_model.IsEnabled) return; // A newer toggle supersedes this queued disable.
            await HideAsync(releaseEditingSession: true);
            if (!_model.IsEnabled && _model.IsVisible) _model.IsEnabled = true; // Failed save keeps the live note usable.
        });
    }
    public void Suspend(bool suspended)
    { if (!_shortcuts.Suspend(suspended)) _model.Notice = _shortcuts.Error; }
    public void Toggle() => Queue(async () =>
    {
        if (!_model.IsEnabled) return;
        if (_model.IsVisible) await HideAsync(); else await ShowAsync();
    });
    public void ToggleFocus() => Queue(() =>
    {
        if (_model.IsEnabled && _model.IsVisible)
        {
            if (_model.IsFocused) Unfocus();
            else { _focus.Capture(); Activate(); }
        }
        return Task.CompletedTask;
    });
    private void Queue(Func<Task> operation)
    {
        if (_stopping || _disposed) return;
        _operation = RunAsync(_operation, operation);
    }
    private async Task RunAsync(Task previous, Func<Task> operation)
    {
        await previous; await Ready;
        if (_stopping || _disposed) return;
        try { await operation(); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        { _model.Notice = "窗口操作失败，请重试。"; }
    }
    private async Task ShowAsync()
    {
        await _model.EnsureContentLoadedAsync();
        if (!_model.IsEnabled || _stopping || _disposed) return;
        _focus.Capture();
        var created = _window is null;
        if (_window is null)
        {
            _window = new(_model);
            _window.BeforeMouseActivate += _focus.Capture;
            _window.UnfocusRequested += Unfocus;
            _window.HideRequested += () => Queue(() => HideAsync());
        }
        _window.Show(); _model.IsVisible = true;
        _window.RecoverPosition(); Activate();
        if (created)
        {
            var window = _window;
            var text = _model.Text;
            var start = Math.Min(_selectionStart, text.Length);
            var length = Math.Min(_selectionLength, Math.Max(0, text.Length - start));
            var offset = _verticalOffset;
            var interacted = false;
            void Interaction(object? sender, RoutedEventArgs e) => interacted = true;
            window.PreviewKeyDown += Interaction;
            window.PreviewMouseDown += Interaction;
            window.PreviewMouseWheel += Interaction;
            window.PreviewTouchDown += Interaction;
            window.PreviewTextInput += Interaction;
            window.NoteEditor.TextChanged += Interaction;
            try
            {
                window.NoteEditor.Select(start, length);
                window.UpdateLayout();
                window.NoteEditor.ScrollToVerticalOffset(offset);
                window.UpdateLayout();
                // Activated/ActivateEditor each queue a deferred FocusEditor. Allow
                // those caret/layout updates to finish before restoring the viewport.
                // New input or document changes always take precedence over restoration.
                await window.Dispatcher.InvokeAsync(() =>
                {
                    if (interacted || _window != window || !_model.IsVisible || !_model.IsEnabled ||
                        _stopping || _disposed || _model.Text != text) return;
                    window.NoteEditor.ScrollToVerticalOffset(offset);
                    window.UpdateLayout();
                }, DispatcherPriority.ContextIdle);
            }
            finally
            {
                window.PreviewKeyDown -= Interaction;
                window.PreviewMouseDown -= Interaction;
                window.PreviewMouseWheel -= Interaction;
                window.PreviewTouchDown -= Interaction;
                window.PreviewTextInput -= Interaction;
                window.NoteEditor.TextChanged -= Interaction;
            }
        }
    }
    private void Activate()
    {
        if (_window?.ActivateEditor() != true)
            _model.Notice = "Windows 未允许浮笺获得输入焦点，请再按一次聚焦快捷键。";
    }
    public void Unfocus()
    {
        if (!_model.IsVisible || !_model.IsFocused) return;
        _window?.ReleaseKeyboard();
        if (!_focus.Restore()) _model.Notice = "先前窗口已关闭或 Windows 未允许切换，请点击要使用的窗口。";
    }
    private async Task HideAsync(bool releaseEditingSession = false)
    {
        if (_window is not null)
        {
            // Read-only/focus changes can bring the caret into view while saving.
            // Capture the user's viewport before freezing the editor.
            _selectionStart = _window.NoteEditor.SelectionStart; _selectionLength = _window.NoteEditor.SelectionLength;
            _verticalOffset = _window.NoteEditor.VerticalOffset;
        }
        // Freeze editing only while flushing, so hide/exit never race with the last keystroke.
        if (_window is not null) _window.NoteEditor.IsReadOnly = true;
        var saved = await _model.FlushAsync();
        if (_window is not null) _window.NoteEditor.IsReadOnly = !_model.ContentWritable;
        if (!saved) return;
        if (_model.IsFocused) Unfocus();
        if (_window is not null)
        {
            _window.Hide(); _model.IsVisible = false; _model.IsFocused = false;
            // Undo/redo is part of the editor session. Keep it while normally hidden;
            // an explicit feature disable is the existing session-ending boundary.
            if (releaseEditingSession || !_window.NoteEditor.CanUndo && !_window.NoteEditor.CanRedo)
            { _window.CloseRuntime(); _window = null; }
        }
        if (_window is null) _model.ReleaseInactiveContent();
    }
    public async Task<bool> PrepareExitAsync()
    {
        _stopping = true; _shortcuts.Suspend(true); await _operation; await Ready;
        if (_window is not null) _window.NoteEditor.IsReadOnly = true;
        if (!await _model.FlushAsync(exiting: true))
        {
            _stopping = false; _shortcuts.Suspend(false);
            if (_window is not null) _window.NoteEditor.IsReadOnly = !_model.ContentWritable;
            return false;
        }
        return true;
    }
    internal void CancelExitPreparation()
    {
        if (_disposed) return;
        _stopping = false; _shortcuts.Suspend(false);
        if (_window is not null) _window.NoteEditor.IsReadOnly = !_model.ContentWritable;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = _stopping = true; _shortcuts.Dispose(); _model.PropertyChanged -= Changed;
        _window?.CloseRuntime(); _window = null; _model.IsVisible = _model.IsFocused = false;
    }
}
