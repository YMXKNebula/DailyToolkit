using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop.ClipRecall;

internal sealed class ClipboardHistoryController : IDisposable
{
    internal const int HotkeyId = 0xB031;
    private readonly ClipboardHistoryViewModel _model;
    private readonly MainViewModel _host;
    private readonly Func<IEnumerable<KeyboardShortcut?>> _reserved;
    private readonly LensShortcutController _shortcut;
    private readonly ClipboardMonitor _monitor;
    private ClipboardCaptureProcessor? _processor;
    private readonly IClipboardAccess _access;
    private readonly SemaphoreSlim _runtimeGate = new(1);
    private readonly INotesForeground _foreground;
    private readonly NotesFocusManager _focus;
    private readonly Func<(int X, int Y)> _cursor;
    private readonly System.Windows.Threading.Dispatcher _dispatcher;
    private ClipboardHistoryPanel? _panel;
    private double? _panelWidth, _panelHeight;
    private Task _configuration = Task.CompletedTask;
    private bool _disposed, _stopping, _openingPanel, _cancelShow, _lastEnabled, _closeForDisable;
    private bool? _panelEnabledBeforeExit;
    public Task Ready { get; }
    internal ClipboardHistoryPanel? Panel => _panel;
    internal void RetargetPreviousWindow(IntPtr previous, IntPtr replacement) => _focus.RetargetPreviousWindow(previous, replacement);
    internal bool IsListening => _monitor.IsListening;
    internal bool IsHotkeyRegistered => _shortcut.IsRegistered;
    internal bool HasCaptureProcessor => _processor is not null;
    internal Task Pending => _configuration;
    internal bool IsOpeningPanel => _openingPanel;
    internal string ConfigurationFailureCode { get; private set; } = "";
    public ClipboardHistoryController(MainViewModel host, WindowMessageSource source, Func<IEnumerable<KeyboardShortcut?>> reserved,
        INotesForeground? foreground = null, IClipboardAccess? access = null, Func<(int X, int Y)>? cursor = null)
    {
        _host = host; _model = host.ClipRecall; _reserved = reserved; _dispatcher = source.Dispatcher;
        _lastEnabled = _model.IsEnabled;
        _foreground = foreground ?? new NotesForeground();
        _cursor = cursor ?? NotesPositionService.Cursor;
        _focus = new(_foreground, () => _panel?.Handle ?? IntPtr.Zero);
        _shortcut = new(source, hotkeyId: HotkeyId); _shortcut.ToggleRequested += Toggle;
        _monitor = new(source); _access = access ?? new ClipboardNativeAccess(source.Handle);
        _monitor.Updated += ClipboardUpdated;
        _model.RestoreRequested = RestoreAsync; _model.PanelRequested = Toggle;
        _model.ConfirmDisable = () => _panel?.ConfirmHide() ?? !_model.IsBusy;
        _model.PropertyChanged += ModelChanged;
        _model.SettingsCommitted += Reconfigure;
        _host.Gaming.PropertyChanged += ReservedChanged; _host.Notes.PropertyChanged += ReservedChanged;
        SystemParameters.StaticPropertyChanged += SystemAppearanceChanged;
        _configuration = Ready = ConfigureAsync(Task.CompletedTask);
    }
    private void ClipboardUpdated() => _processor?.Notify();
    private ClipboardCaptureProcessor CreateProcessor()
    {
        if (_processor is null)
        {
            _processor = new(_model.Repository, _access);
            _processor.Status += UpdateStatus;
        }
        return _processor;
    }
    private async Task ReleaseProcessorAsync()
    {
        var processor = _processor;
        if (processor is null) return;
        processor.Configure(_model.Settings with { IsEnabled = false });
        processor.Status -= UpdateStatus;
        await processor.DisposeAsync();
        if (ReferenceEquals(_processor, processor)) _processor = null;
    }
    private void UpdateStatus(string notice) => _dispatcher.BeginInvoke(new Action(() => { if (!_disposed) _model.Notice = notice; }));
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ClipboardHistoryViewModel.IsBusy) && !_model.IsBusy && !_model.IsEnabled && !_model.IsVisible)
        { Reconfigure(); return; }
        if (e.PropertyName == nameof(ClipboardHistoryViewModel.Appearance))
        { if (_panel is not null) _model.ApplyAppearance(_panel.Resources); return; }
        if (e.PropertyName is not ("" or nameof(ClipboardHistoryViewModel.Settings))) return;
        if (_lastEnabled && !_model.IsEnabled) { _closeForDisable = true; _cancelShow = true; }
        if (_model.IsEnabled) _closeForDisable = false;
        _lastEnabled = _model.IsEnabled;
        // Stop immediately on user pause/disable. Enabling waits for committed settings.
        if (!_model.IsEnabled || _model.IsPaused)
        {
            _monitor.Configure(false); _processor?.Configure(_model.Settings);
            Reconfigure();
        }
        else if (!_model.UseCachedFiles) _processor?.Configure(_model.Settings);
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
        await _runtimeGate.WaitAsync();
        try
        {
            if (_disposed || _stopping) return;
            if (!_model.IsEnabled)
            {
                _monitor.Configure(false); _shortcut.Configure(null, LensActivationMode.Toggle);
                if (_closeForDisable && !Hide(restoreFocus: true, scheduleRelease: false))
                { _model.RequestEnabled(true); _model.Notice = "停用已取消，请先完成当前编辑。"; return; }
                _closeForDisable = false;
                await ReleaseProcessorAsync();
                return;
            }
            if (_model.IsEnabled) { await _model.EnsureRepositoryAsync(); await _model.Repository.CleanupAsync(_model.Settings); }
            if (_disposed || _stopping) return;
            var settings = _model.Settings;
            if (!settings.IsEnabled || settings.IsPaused) await ReleaseProcessorAsync();
            else CreateProcessor().Configure(settings);
            _monitor.Configure(settings.IsEnabled && !settings.IsPaused);
            var shortcut = settings.ToggleShortcut;
            if (shortcut is not null && _reserved().Any(shortcut.Matches))
            { shortcut = null; _model.Notice = "剪贴回溯快捷键与现有工具冲突，请更换快捷键。"; }
            if (!_shortcut.Configure(settings.IsEnabled ? shortcut : null, LensActivationMode.Toggle))
                _model.Notice = "剪贴回溯快捷键注册失败，可能已被其他程序占用；请更换快捷键。";
            if (!settings.IsEnabled && _closeForDisable) { Hide(restoreFocus: true, scheduleRelease: false); _closeForDisable = false; }
        }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
        {
            // Fixed type/code metadata only; never retain clipboard bodies or paths.
            ConfigurationFailureCode = exception is System.ComponentModel.Win32Exception native
                ? $"Win32:{native.NativeErrorCode}" : $"{exception.GetType().Name}:0x{exception.HResult:X8}";
            _monitor.Configure(false); _shortcut.Configure(null, LensActivationMode.Toggle);
            await ReleaseProcessorAsync();
            Runtime.DiagnosticReportService.Shared.RecordError(Runtime.DiagnosticModule.ClipRecall, exception);
            _model.Notice = "历史库打开失败，监听已停止。请检查数据目录或恢复备份。";
        }
        finally
        {
            _runtimeGate.Release();
            // Model actions may wait for RestoreAsync; release its gate first.
            if (!_disposed && !_model.IsEnabled) await _model.ReleaseRuntimeAsync();
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
        if (_panel?.IsVisible == true && _panel.WindowState != WindowState.Minimized) { Hide(restoreFocus: true); return; }
        _openingPanel = true; _cancelShow = false;
        // Explicit browsing is active use even while recording is disabled.
        // Mark it before asynchronous opening so an idle release cannot close it.
        _model.IsVisible = true;
        var entered = false;
        try
        {
            await _configuration;
            await _runtimeGate.WaitAsync(); entered = true;
            await _model.EnsureRepositoryAsync();
            if (_disposed || _stopping || _cancelShow) return;
            _focus.Capture();
            if (_panel is null)
            {
                _panel = new(_model); _model.ApplyAppearance(_panel.Resources);
                if (_panelWidth is { } width) _panel.Width = width;
                if (_panelHeight is { } height) _panel.Height = height;
                _panel.HideRequested += () => Hide(restoreFocus: true);
            }
            _model.IsVisible = true;
            _panel.ShowNearPoint(_cursor());
            _panel.ActivatePanel();
        }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
        {
            ConfigurationFailureCode = exception is System.ComponentModel.Win32Exception native
                ? $"Win32:{native.NativeErrorCode}" : $"{exception.GetType().Name}:0x{exception.HResult:X8}";
            Runtime.DiagnosticReportService.Shared.RecordError(Runtime.DiagnosticModule.ClipRecall, exception);
            _model.Notice = "历史面板未能打开，原数据保留。请检查数据目录。";
        }
        finally
        {
            _openingPanel = false;
            if (entered) _runtimeGate.Release();
            if (_panel?.IsVisible != true)
            {
                _model.IsVisible = false;
                Hide(restoreFocus: false, scheduleRelease: false);
                if (!_disposed && !_stopping && !_model.IsEnabled) Reconfigure();
            }
        }
    }
    private bool Hide(bool restoreFocus, bool scheduleRelease = true)
    {
        if (_panel is null) return true;
        if (_panel.IsVisible && !_stopping && !_panel.ConfirmHide()) return false;
        var ownsFocus = _panel.IsVisible && _foreground.Current == _panel.Handle;
        var panel = _panel; _panel = null;
        _panelWidth = panel.ActualWidth > 0 ? panel.ActualWidth : panel.Width;
        _panelHeight = panel.ActualHeight > 0 ? panel.ActualHeight : panel.Height;
        _model.IsVisible = false; panel.CloseRuntime();
        // A user who already activated another app keeps that focus. No attachment
        // to input threads, synthetic keypress, window restoration or polling.
        if (restoreFocus && ownsFocus && !_focus.Restore())
            _model.Notice = "无法返回原窗口，请点击要使用的窗口。";
        if (scheduleRelease && !_model.IsEnabled) Reconfigure();
        return true;
    }
    private async Task<ClipboardOperationResult> RestoreAsync(ClipboardHistoryRecord record)
    {
        await _runtimeGate.WaitAsync();
        try
        {
            if (_disposed || _stopping) return new(false, "剪贴回溯正在退出。");
            var processor = CreateProcessor(); processor.Configure(_model.Settings);
            return await processor.RestoreAsync(record);
        }
        finally
        {
            try { if (!_model.IsEnabled || _model.IsPaused) await ReleaseProcessorAsync(); }
            finally { _runtimeGate.Release(); }
        }
    }
    public async Task<bool> PrepareExitAsync()
    {
        if (_panel?.IsVisible == true && !_panel.ConfirmHide()) return false;
        _stopping = true; _monitor.Configure(false); _shortcut.Suspend(true);
        if (_panel is not null) { _panelEnabledBeforeExit ??= _panel.IsEnabled; _panel.IsEnabled = false; }
        var prepared = false;
        try
        {
            await _configuration; await Ready;
            if (!await _model.PrepareExitAsync()) return false;
            await _runtimeGate.WaitAsync();
            try { await ReleaseProcessorAsync(); }
            finally { _runtimeGate.Release(); }
            prepared = true;
            return true;
        }
        finally { if (!prepared) CancelExitPreparation(); }
    }
    internal void CancelExitPreparation()
    {
        if (_disposed || !_stopping) return;
        _model.CancelExitPreparation();
        _stopping = false;
        if (_panel is not null && _panelEnabledBeforeExit is { } enabled) _panel.IsEnabled = enabled;
        _panelEnabledBeforeExit = null;
        _shortcut.Suspend(false);
        Reconfigure();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = _stopping = true;
        _monitor.Dispose(); _shortcut.Dispose();
        _model.PropertyChanged -= ModelChanged; _model.SettingsCommitted -= Reconfigure;
        _model.RestoreRequested = null; _model.PanelRequested = null; _model.ConfirmDisable = null;
        _host.Gaming.PropertyChanged -= ReservedChanged; _host.Notes.PropertyChanged -= ReservedChanged;
        SystemParameters.StaticPropertyChanged -= SystemAppearanceChanged;
        _panel?.CloseRuntime(); _panel = null; _model.IsVisible = false;
        _ = DisposeProcessorAsync();
    }
    private async Task DisposeProcessorAsync()
    {
        await _runtimeGate.WaitAsync();
        try { await ReleaseProcessorAsync(); }
        finally { _runtimeGate.Release(); }
    }
}
