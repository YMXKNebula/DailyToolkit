using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.FilePack;

internal sealed class FilePackController : IDisposable
{
    internal const int HotkeyId = 0xB041;
    private const string ShortcutInUseNotice = "快捷键已被占用，请更换组合。";
    private readonly MainViewModel _host;
    private readonly FilePackViewModel _model;
    private readonly Func<IEnumerable<KeyboardShortcut?>> _reserved;
    private readonly LensShortcutController _shortcut;
    private readonly INotesForeground _foreground;
    private readonly NotesFocusManager _focus;
    private readonly Func<(int X, int Y)> _cursor;
    private bool _disposed, _opening, _cancelShow, _readyForRegistration;
    private readonly HashSet<FilePackWindow> _closingWindows = [];
    private FilePackWindowSession? _quickSession, _detailsSession;
    private FilePackWindow? _quick, _details;
    internal FilePackWindow? QuickWindow => _quick;
    internal FilePackWindow? PackWindow => _details;
    internal bool IsHotkeyRegistered => _shortcut.IsRegistered;
    public Task Ready { get; }
    internal event Action<object?>? LegacyShortcutReleased;
    public FilePackController(MainViewModel host, HwndSource source, Func<IEnumerable<KeyboardShortcut?>> reserved, INotesForeground? foreground = null,
        Func<(int X, int Y)>? cursor = null)
    {
        _host = host; _model = host.FilePack; _reserved = reserved;
        _foreground = foreground ?? new NotesForeground();
        _cursor = cursor ?? NotesPositionService.Cursor;
        _focus = new(_foreground, () => _quick?.IsActive == true ? _quick.Handle : _details?.Handle ?? IntPtr.Zero);
        _shortcut = new(source, hotkeyId: HotkeyId); _shortcut.ToggleRequested += ToggleQuick;
        _model.QuickWindowRequested = ToggleQuick; _model.PackWindowRequested = OpenDetails;
        _model.PropertyChanged += Changed;
        _model.RuntimeReleased += CloseAllWindows;
        _model.SetToolWindowsVisible(false);
        _host.Gaming.PropertyChanged += ReservedChanged; _host.Notes.PropertyChanged += ReservedChanged; _host.ClipRecall.PropertyChanged += ReservedChanged;
        Ready = InitializeAsync();
    }
    private async Task InitializeAsync()
    {
        await Task.WhenAll(_model.SettingsReady, _host.Notes.Ready, _host.ClipRecall.Ready);
        _readyForRegistration = true; Reconfigure();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is "" or nameof(FilePackViewModel.IsEnabled) or nameof(FilePackViewModel.ToggleShortcut)) Reconfigure(); }
    private void ReservedChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not ("" or "ToggleShortcut" or "Preferences" or "Settings" or "IsEnabled")) return;
        var registered = _shortcut.IsRegistered; Reconfigure();
        if (registered && !_shortcut.IsRegistered) LegacyShortcutReleased?.Invoke(sender);
    }
    private void Reconfigure()
    {
        if (_disposed || !_readyForRegistration || !_model.SettingsLoaded) return;
        var shortcut = _model.IsEnabled ? _model.ToggleShortcut : null;
        var notice = "";
        if (shortcut is not null && _reserved().Any(shortcut.Matches))
        { shortcut = null; notice = "此快捷键与其他工具冲突。"; }
        if (!_shortcut.Configure(shortcut, LensActivationMode.Toggle)) notice = ShortcutInUseNotice;
        _model.SetShortcutNotice(notice);
        if (!_model.IsEnabled) _cancelShow = true;
    }
    public void Suspend(bool suspended)
    {
        if (!_shortcut.Suspend(suspended)) _model.SetShortcutNotice(ShortcutInUseNotice);
        else if (!suspended && _shortcut.IsRegistered) _model.SetShortcutNotice("");
    }
    public async void ToggleQuick()
    {
        if (_disposed || _model.IsStopping || !_model.IsEnabled) return;
        if (_opening) { _cancelShow = !_cancelShow; return; }
        if (_quick?.IsVisible == true && _quick.WindowState != WindowState.Minimized) { await CloseWindowAsync(_quick, true); return; }
        _opening = true; _cancelShow = false;
        try { await _model.InitializeAsync(); if (_disposed || _model.IsStopping || _cancelShow || !_model.IsEnabled) return; ShowQuick(); }
        catch (Exception exception) when (FilePackViewModel.IsExpected(exception)) { _model.SetNotice("快速窗口未能打开。"); }
        finally { _opening = false; }
    }
    private void CaptureFocus()
    { if (_foreground.Current != _quick?.Handle && _foreground.Current != _details?.Handle) _focus.Capture(); }
    private void ShowQuick()
    {
        CaptureFocus();
        var created = _quick is null;
        if (_quick is null) _quick = CreateWindow(quick: true);
        _quick.ShowNearPoint(_cursor());
        if (created) _quick.RestoreSession(_quickSession);
        UpdateWindowVisibility();
    }
    public async void OpenDetails()
    {
        if (_disposed || _model.IsStopping || !_model.IsEnabled) return;
        try
        {
            await _model.InitializeAsync(); if (_disposed || _model.IsStopping || !_model.IsEnabled) return;
            CaptureFocus(); if (!await CloseWindowAsync(_quick, false)) return;
            if (_disposed || _model.IsStopping || !_model.IsEnabled) return;
            var created = _details is null;
            if (_details is null) _details = CreateWindow(quick: false);
            if (_details.IsVisible && _details.WindowState != WindowState.Minimized) _details.Activate(); else _details.ShowNearPoint(_cursor());
            if (created) _details.RestoreSession(_detailsSession);
            UpdateWindowVisibility();
        }
        catch (Exception exception) when (FilePackViewModel.IsExpected(exception)) { _model.SetNotice("打包窗口未能打开。"); }
    }
    internal async Task<bool> AddShellPathsAsync(IReadOnlyList<string> paths)
    {
        await _model.SettingsReady;
        if (_disposed) return false;
        if (!_model.IsEnabled) { _host.Page = "file-pack"; _model.SetNotice("请先开启文件集装箱。"); return false; }
        await _model.InitializeAsync(); if (_disposed) return false;
        await _model.WaitForDraftAsync();
        if (_disposed) return false;
        if (!_model.IsEnabled) { _host.Page = "file-pack"; _model.SetNotice("请先开启文件集装箱。"); return false; }
        if (!_model.CanEditDraft) { _model.SetNotice("正在收集或提交文件，请稍后重试。"); return false; }
        ShowQuick(); await _model.AddPathsAsync(paths); return true;
    }
    private FilePackWindow CreateWindow(bool quick)
    {
        var window = new FilePackWindow(_model, quick);
        var size = quick ? _quickSession : _detailsSession;
        if (size is { } saved) { window.Width = saved.Width; window.Height = saved.Height; }
        window.HideRequested += () => _ = CloseWindowAsync(window, true);
        window.IsVisibleChanged += (_, _) => UpdateWindowVisibility();
        return window;
    }
    private void UpdateWindowVisibility() => _model.SetToolWindowsVisible(_quick?.IsVisible == true || _details?.IsVisible == true);
    private async Task<bool> CloseWindowAsync(FilePackWindow? window, bool restore)
    {
        if (window is null) return true;
        if (!_closingWindows.Add(window)) return false;
        try
        {
            if (!await _model.SaveBeforeClosingWindowAsync()) return false;
            if (!ReferenceEquals(window, _quick) && !ReferenceEquals(window, _details)) return true;
            var ownsFocus = _foreground.Current == window.Handle;
            ReleaseWindow(window);
            if (restore && ownsFocus) _focus.Restore();
            return true;
        }
        finally { _closingWindows.Remove(window); }
    }
    private void ReleaseWindow(FilePackWindow window)
    {
        if (ReferenceEquals(window, _quick)) { _quickSession = window.CaptureSession(); _quick = null; }
        if (ReferenceEquals(window, _details)) { _detailsSession = window.CaptureSession(); _details = null; }
        window.CloseRuntime(); UpdateWindowVisibility();
    }
    private void CloseAllWindows()
    {
        var quick = _quick; var details = _details;
        if (quick is not null) ReleaseWindow(quick);
        if (details is not null) ReleaseWindow(details);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _shortcut.Dispose(); _model.PropertyChanged -= Changed;
        _model.RuntimeReleased -= CloseAllWindows;
        _host.Gaming.PropertyChanged -= ReservedChanged; _host.Notes.PropertyChanged -= ReservedChanged; _host.ClipRecall.PropertyChanged -= ReservedChanged;
        _model.QuickWindowRequested = null; _model.PackWindowRequested = null;
        CloseAllWindows();
    }
}
