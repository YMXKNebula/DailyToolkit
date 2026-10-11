using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.ClipRecall;

internal sealed class ClipboardHistoryController : IDisposable
{
    internal const int HotkeyId = 0xB031;
    private readonly ClipboardHistoryViewModel _model;
    private readonly MainViewModel _host;
    private readonly Func<IEnumerable<KeyboardShortcut?>> _reserved;
    private readonly LensShortcutController _shortcut;
    private readonly ClipboardMonitor _monitor;
    private readonly ClipboardCaptureProcessor _processor;
    private readonly INotesForeground _foreground;
    private readonly NotesFocusManager _focus;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private ClipboardHistoryPanel? _panel;
    private Task _configuration = Task.CompletedTask;
    private bool _disposed, _stopping, _openingPanel, _cancelShow;
    public Task Ready { get; }
    internal ClipboardHistoryPanel? Panel => _panel;
    internal bool IsListening => _monitor.IsListening;
    internal bool IsHotkeyRegistered => _shortcut.IsRegistered;
    internal Action? SettingsRequested { get; set; }
    public ClipboardHistoryController(MainViewModel host, HwndSource source, Func<IEnumerable<KeyboardShortcut?>> reserved,
        INotesForeground? foreground = null, IClipboardAccess? access = null)
    {
        _host = host; _model = host.ClipRecall; _reserved = reserved; _dispatcher = source.Dispatcher;
        _foreground = foreground ?? new NotesForeground();
        _focus = new(_foreground, () => _panel?.Handle ?? IntPtr.Zero);
        _shortcut = new(source, hotkeyId: HotkeyId); _shortcut.ToggleRequested += Toggle;
        _monitor = new(source); _processor = new(_model.Repository, access ?? new ClipboardNativeAccess(source.Handle));
        _monitor.Updated += _processor.Notify;
        _processor.Status += UpdateStatus;
        _model.RestoreRequested = RestoreAsync; _model.PanelRequested = Toggle;
        _model.PropertyChanged += ModelChanged;
        _model.SettingsCommitted += Reconfigure;
        _host.Gaming.PropertyChanged += ReservedChanged; _host.Notes.PropertyChanged += ReservedChanged;
        SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        _configuration = Ready = ConfigureAsync(Task.CompletedTask);
    }
    private void UpdateStatus(string notice) => _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) _model.Notice = notice; }));
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClipboardHistoryViewModel.Appearance))
        { if (_panel is not null) _model.ApplyAppearance(_panel.Resources); return; }
        if (e.PropertyName is not ("" or nameof(ClipboardHistoryViewModel.Settings))) return;
        // Stop immediately on user pause/disable. Enabling waits for committed settings.
        if (!_model.IsEnabled || _model.IsPaused) { _monitor.Configure(false); _processor.Configure(_model.Settings); }
        else if (!_model.UseCachedFiles) _processor.Configure(_model.Settings);
    }
    private void ReservedChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(GamingViewModel.ToggleShortcut) or nameof(NotesViewModel.Preferences) or "") Reconfigure(); }
    private void SystemAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SystemParameters.HighContrast)) _dispatcher.BeginInvoke(new Action(() => { if (!_disposed && _panel is not null) _model.ApplyAppearance(_panel.Resources); })); }
    internal void Reconfigure()
    {
        if (_disposed || _stopping) return;
        _configuration = ConfigureAsync(_configuration);
    }
    private async Task ConfigureAsync(Task previous)
    {
        await previous; await _model.Ready;
        if (_disposed || _stopping) return;
        try
        {
            if (_model.IsEnabled) { await _model.EnsureRepositoryAsync(); await _model.Repository.CleanupAsync(_model.Settings); }
            if (_disposed || _stopping) return;
            var settings = _model.Settings;
            _processor.Configure(settings);
            _monitor.Configure(settings.IsEnabled && !settings.IsPaused);
            var shortcut = settings.ToggleShortcut;
            if (shortcut is not null && _reserved().Any(shortcut.Matches))
            { shortcut = null; _model.Notice = "剪贴回溯快捷键与现有工具冲突，请更换快捷键。"; }
            if (!_shortcut.Configure(settings.IsEnabled ? shortcut : null, LensActivationMode.Toggle))
                _model.Notice = "剪贴回溯快捷键注册失败，可能已被其他程序占用；请更换快捷键。";
            if (!settings.IsEnabled) Hide(restoreFocus: true);
            if (!settings.IsEnabled || settings.IsPaused) await _processor.ReleaseIdleWorkerAsync();
        }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
        {
            _monitor.Configure(false); _shortcut.Configure(null, LensActivationMode.Toggle);
            _model.Notice = "剪贴回溯未能启动，监听已停止，原数据保留。请检查数据目录或恢复备份。";
        }
    }
    public void Suspend(bool suspended)
    {
        if (!_shortcut.Suspend(suspended)) _model.Notice = "剪贴回溯快捷键注册失败，请更换快捷键。";
    }
    public async void Toggle()
    {
        if (_disposed || _stopping) return;
        if (_openingPanel) { _cancelShow = !_cancelShow; return; }
        if (_panel?.IsVisible == true) { Hide(restoreFocus: true); return; }
        _openingPanel = true; _cancelShow = false;
        try
        {
            await _model.EnsureRepositoryAsync();
            if (_disposed || _stopping || _cancelShow) return;
            _focus.Capture();
            if (_panel is null)
            {
                _panel = new(_model); _model.ApplyAppearance(_panel.Resources);
                _panel.HideRequested += () => Hide(restoreFocus: true);
                _panel.SettingsRequested += () => { if (Hide(restoreFocus: false)) SettingsRequested?.Invoke(); };
            }
            _model.IsVisible = true;
            _panel.ShowNearCursor();
            if (!_panel.ActivateSearch()) _model.Notice = "Windows 未允许面板获得焦点，请点击搜索框。";
        }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
        { _model.Notice = "历史面板未能打开，原数据保留。请检查数据目录。"; }
        finally { _openingPanel = false; }
    }
    private bool Hide(bool restoreFocus)
    {
        if (_panel?.IsVisible != true) return true;
        if (!_stopping && !_panel.ConfirmHide()) return false;
        var ownsFocus = _foreground.Current == _panel.Handle;
        _panel.Hide(); _model.IsVisible = false;
        // A user who already activated another app keeps that focus. No attachment
        // to input threads, synthetic keypress, window restoration or polling.
        if (restoreFocus && ownsFocus && !_focus.Restore())
            _model.Notice = "面板已关闭；先前窗口已关闭或 Windows 未允许恢复焦点。";
        return true;
    }
    private async Task<ClipboardOperationResult> RestoreAsync(ClipboardHistoryRecord record)
    {
        var result = await _processor.RestoreAsync(record);
        if (!_processor.IsCapturing) await _processor.ReleaseIdleWorkerAsync();
        return result;
    }
    public async Task<bool> PrepareExitAsync()
    {
        if (_panel?.IsVisible == true && !_panel.ConfirmHide()) return false;
        _stopping = true; _monitor.Configure(false); _shortcut.Suspend(true); await _configuration; await Ready;
        if (!await _model.PrepareExitAsync()) { _stopping = false; _shortcut.Suspend(false); Reconfigure(); return false; }
        await _processor.DisposeAsync(); await _model.DisposeAsync(); return true;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = _stopping = true;
        _monitor.Dispose(); _shortcut.Dispose(); _processor.Status -= UpdateStatus;
        _model.PropertyChanged -= ModelChanged; _model.SettingsCommitted -= Reconfigure;
        _model.RestoreRequested = null; _model.PanelRequested = null;
        _host.Gaming.PropertyChanged -= ReservedChanged; _host.Notes.PropertyChanged -= ReservedChanged;
        SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        _panel?.CloseRuntime(); _panel = null; _model.IsVisible = false;
        _ = _processor.DisposeAsync();
    }
}
