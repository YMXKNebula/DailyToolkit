using System.ComponentModel;
using System.Windows.Interop;
using DailyToolkit.Core.Gaming;

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
    public Task Ready { get; }
    internal NotesWindow? NoteWindow => _window;
    internal IntPtr RestoreTarget => _focus.Previous;
    internal Task Pending => _operation;

    public NotesController(NotesViewModel model, HwndSource source, Func<KeyboardShortcut?> lensShortcut,
        INotesForeground? foreground = null)
    {
        _model = model; _lensShortcut = lensShortcut; _shortcuts = new(source);
        _focus = new(foreground ?? new NotesForeground(), () => _window?.Handle ?? IntPtr.Zero);
        _shortcuts.ToggleRequested += Toggle;
        _shortcuts.FocusRequested += Refocus;
        _model.PropertyChanged += Changed;
        Ready = InitializeAsync();
    }
    private async Task InitializeAsync() { await _model.Ready; if (!_disposed) Reconfigure(); }
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
        var previouslyEnabled = _registered?.Enabled == true; _registered = next;
        var toggle = preferences.ToggleShortcut; var focus = preferences.FocusShortcut;
        if (next.Item4 is not null)
        {
            if (next.Item4.Matches(toggle)) toggle = null;
            if (next.Item4.Matches(focus)) focus = null;
            if (toggle != preferences.ToggleShortcut || focus != preferences.FocusShortcut)
                _model.Notice = "浮笺快捷键与屏幕局部放大冲突，请修改浮笺快捷键。";
        }
        var success = _shortcuts.Configure(preferences.IsEnabled ? toggle : null, preferences.IsEnabled ? focus : null);
        if (!success) _model.Notice = _shortcuts.Error;
        else if (_model.Notice.StartsWith("浮笺快捷键注册失败", StringComparison.Ordinal)) _model.Notice = "";
        if (previouslyEnabled && !preferences.IsEnabled) Queue(async () =>
        {
            await HideAsync();
            if (!_model.IsEnabled && _model.IsVisible) _model.IsEnabled = true; // Failed save keeps the live note usable.
            if (!_model.IsEnabled && _window is not null && !_model.IsVisible) { _window.CloseRuntime(); _window = null; }
        });
    }
    public void Suspend(bool suspended)
    { if (!_shortcuts.Suspend(suspended)) _model.Notice = _shortcuts.Error; }
    public void Toggle() => Queue(async () =>
    {
        if (!_model.IsEnabled) return;
        if (_model.IsVisible) await HideAsync(); else Show();
    });
    public void Refocus() => Queue(() =>
    {
        if (_model.IsEnabled && _model.IsVisible && !_model.IsFocused)
        { _focus.Capture(); Activate(); }
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
        { _model.Notice = "浮笺窗口操作未完成，请重试；笔记内容已保留。"; }
    }
    private void Show()
    {
        _focus.Capture();
        if (_window is null)
        {
            _window = new(_model);
            _window.BeforeMouseActivate += _focus.Capture;
            _window.UnfocusRequested += Unfocus;
            _window.HideRequested += () => Queue(HideAsync);
        }
        _window.Show(); _model.IsVisible = true;
        _window.RecoverPosition(); Activate();
    }
    private void Activate()
    {
        if (_window?.ActivateEditor() != true)
            _model.Notice = "Windows 未允许浮笺获得输入焦点，请直接点击浮笺正文。";
    }
    public void Unfocus()
    {
        if (!_model.IsVisible || !_model.IsFocused) return;
        _window?.ReleaseKeyboard();
        if (!_focus.Restore()) _model.Notice = "先前窗口已关闭或 Windows 未允许切换；浮笺已停止接收输入，请点击要继续使用的窗口。";
    }
    private async Task HideAsync()
    {
        // Freeze editing only while flushing, so hide/exit never race with the last keystroke.
        if (_window is not null) _window.NoteEditor.IsReadOnly = true;
        var saved = await _model.FlushAsync();
        if (_window is not null) _window.NoteEditor.IsReadOnly = !_model.ContentWritable;
        if (!saved) return;
        if (_model.IsFocused) Unfocus();
        _window?.Hide(); _model.IsVisible = false; _model.IsFocused = false;
    }
    public async Task<bool> PrepareExitAsync()
    {
        _stopping = true; _shortcuts.Suspend(true); await _operation; await Ready;
        if (_window is not null) _window.NoteEditor.IsReadOnly = true;
        if (!await _model.FlushAsync())
        {
            _stopping = false; _shortcuts.Suspend(false);
            if (_window is not null) _window.NoteEditor.IsReadOnly = !_model.ContentWritable;
            return false;
        }
        return true;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = _stopping = true; _shortcuts.Dispose(); _model.PropertyChanged -= Changed;
        _window?.CloseRuntime(); _window = null; _model.IsVisible = _model.IsFocused = false;
    }
}
