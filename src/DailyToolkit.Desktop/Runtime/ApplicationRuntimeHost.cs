using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.FilePack;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Runtime;

// The message window and tool controllers outlive every main-window visual tree.
internal sealed partial class ApplicationRuntimeHost : IDisposable
{
    private const string HotkeyConflictNotice = "快捷键注册失败，可能已被其他程序占用。";
    private readonly MainViewModel _viewModel;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _idleTimer;
    private readonly TimeSpan _idleDelay;
    private readonly bool _shutdownApplication;
    private NativeMessageWindow? _source;
    private TrayIcon? _tray;
    private MainWindowUiState? _uiState;
    private IntPtr _releasedMainWindowHandle;
    private WindowState _lastVisibleState = WindowState.Normal;
    private bool _disposed;

    internal MainWindow? Window { get; private set; }
    internal IntPtr SourceHandle => _source?.Handle ?? IntPtr.Zero;
    internal LensShortcutController? LensShortcuts { get; private set; }
    internal NotesController? Notes { get; private set; }
    internal ClipboardHistoryController? ClipRecall { get; private set; }
    internal FilePackController? FilePack { get; private set; }
    internal bool TrayAvailable => _tray?.Available == true;
    internal event Action<MainWindow>? WindowCreated;

    internal ApplicationRuntimeHost(MainViewModel viewModel, bool enableRuntime = true,
        TimeSpan? idleDelay = null, bool shutdownApplication = true)
    {
        _viewModel = viewModel;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _idleDelay = idleDelay ?? TimeSpan.FromSeconds(5);
        if (_idleDelay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(idleDelay));
        _shutdownApplication = shutdownApplication;
        _idleTimer = new(DispatcherPriority.Background, _dispatcher) { Interval = _idleDelay };
        _idleTimer.Tick += ReleaseHiddenWindow;
        try
        {
            _viewModel.SetMainWindowVisible(false);
            InitializeRuntime(enableRuntime);
            _viewModel.AdministratorRestartRequested += RequestExit;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void InitializeRuntime(bool enableRuntime)
    {
        // EnumWindows must find this top-level HWND even while no WPF Window exists.
        // No root visual, WS_VISIBLE or message-only parent is attached.
        _source = new();
        _source.AddHook(RuntimeMessage);
        if (!SetProp(_source.Handle, SingleInstance.WindowMarker, new IntPtr(1)))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        ChangeWindowMessageFilterEx(_source.Handle, SingleInstance.ShowMessage, 1, IntPtr.Zero);
        ChangeWindowMessageFilterEx(_source.Handle, SingleInstance.ExitMessage, 1, IntPtr.Zero);
        if (!enableRuntime) return;

        LensShortcuts = new(_source);
        _viewModel.Shortcuts.ObserveRegistration(ShortcutIds.LensToggle, LensShortcuts);
        LensShortcuts.ToggleRequested += _viewModel.Gaming.Toggle;
        LensShortcuts.StartRequested += _viewModel.Gaming.Start;
        LensShortcuts.StopRequested += _viewModel.Gaming.Stop;
        RegisterLensHotkey();
        Notes = new(_viewModel.Notes, _source, () => _viewModel.Gaming.ToggleShortcut, shortcuts: _viewModel.Shortcuts);
        ClipRecall = new(_viewModel, _source, ExistingClipboardShortcuts);
        FilePack = new(_viewModel, _source, ReservedFilePackShortcuts);
        FilePack.LegacyShortcutReleased += LegacyShortcutReleased;
        _viewModel.Gaming.PropertyChanged += GamingChanged;
        _tray = new(_source);
        _tray.ShowRequested += ShowWindow;
        _tray.SettingsRequested += ShowSettings;
        _tray.ExitRequested += RequestExit;
        _viewModel.RefreshStartupRegistration();
    }

    private IEnumerable<KeyboardShortcut?> ExistingClipboardShortcuts() =>
        [_viewModel.Gaming.ToggleShortcut, _viewModel.Notes.Preferences.ToggleShortcut,
            _viewModel.Notes.Preferences.FocusShortcut];

    private IEnumerable<KeyboardShortcut?> ReservedFilePackShortcuts() =>
        [_viewModel.Gaming.ToggleShortcut, _viewModel.Notes.Preferences.ToggleShortcut,
            _viewModel.Notes.Preferences.FocusShortcut,
            _viewModel.ClipRecall.IsEnabled ? _viewModel.ClipRecall.Settings.ToggleShortcut : null];

    private void GamingChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(GamingViewModel.ToggleShortcut) or
            nameof(GamingViewModel.ActivationMode) or nameof(GamingViewModel.IsEnabled))) return;
        RegisterLensHotkey();
        Notes?.Reconfigure();
    }

    internal void RegisterLensHotkey()
    {
        if (_disposed || LensShortcuts is null) return;
        UpdateShortcutNotice(LensShortcuts.Configure(
            _viewModel.Gaming.IsEnabled ? _viewModel.Gaming.ToggleShortcut : null,
            _viewModel.Gaming.ActivationMode));
    }

    internal void SuspendToolShortcuts(bool suspended)
    {
        if (_disposed) return;
        if (LensShortcuts is not null) UpdateShortcutNotice(LensShortcuts.Suspend(suspended));
        Notes?.Suspend(suspended);
        ClipRecall?.Suspend(suspended);
        FilePack?.Suspend(suspended);
    }

    private void UpdateShortcutNotice(bool success)
    {
        if (!success) _viewModel.Notice = HotkeyConflictNotice;
        else if (_viewModel.Notice == HotkeyConflictNotice) _viewModel.Notice = "";
    }

    private void LegacyShortcutReleased(object? sender)
    {
        if (ReferenceEquals(sender, _viewModel.Gaming)) RegisterLensHotkey();
        else if (ReferenceEquals(sender, _viewModel.Notes)) Notes?.Suspend(false);
        else if (ReferenceEquals(sender, _viewModel.ClipRecall)) ClipRecall?.Suspend(false);
    }

    internal void ShowWindow()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || Window?.IsExitInProgress == true) return;
        CancelIdleMemoryReclaim();
        _idleTimer.Stop();
        var current = GetOrCreateWindow();
        if (!current.IsVisible) current.Show();
        if (current.WindowState == WindowState.Minimized) current.WindowState = _lastVisibleState;
        current.Activate();
    }

    private MainWindow GetOrCreateWindow()
    {
        if (Window is { } existing) return existing;
        var window = new MainWindow(_viewModel, this);
        Window = window;
        window.IsVisibleChanged += WindowVisibilityChanged;
        window.StateChanged += WindowStateChanged;
        window.SourceInitialized += MainWindowSourceInitialized;
        window.Closed += WindowClosed;
        if (_uiState is not null) window.RestoreUiState(_uiState);
        if (Application.Current is { } application) application.MainWindow = window;
        WindowCreated?.Invoke(window);
        return window;
    }

    private void MainWindowSourceInitialized(object? sender, EventArgs args)
    {
        if (sender is not MainWindow window || !ReferenceEquals(Window, window)) return;
        window.SourceInitialized -= MainWindowSourceInitialized;
        var previous = _releasedMainWindowHandle;
        var replacement = new WindowInteropHelper(window).Handle;
        if (previous == IntPtr.Zero || replacement == IntPtr.Zero) return;
        Notes?.RetargetPreviousWindow(previous, replacement);
        ClipRecall?.RetargetPreviousWindow(previous, replacement);
        FilePack?.RetargetPreviousWindow(previous, replacement);
        _releasedMainWindowHandle = IntPtr.Zero;
    }

    internal void ShowSettings()
    {
        if (_disposed || Window?.IsExitInProgress == true) return;
        ShowWindow();
        if (!_disposed && !_viewModel.IsSettings) _viewModel.OpenSettingsCommand.Execute(null);
    }

    internal void RequestExit()
    {
        _dispatcher.VerifyAccess();
        if (_disposed) return;
        CancelIdleMemoryReclaim();
        _idleTimer.Stop();
        var window = GetOrCreateWindow();
        // A successful tray exit never flashes the main interface. The existing
        // close/save flow shows it itself if saving fails or user action is needed.
        new WindowInteropHelper(window).EnsureHandle();
        window.RequestExit();
        if (ReferenceEquals(Window, window) && !window.IsVisible && !window.IsExitInProgress)
        {
            // Cancelling the synchronous task confirmation may leave this newly
            // created UI hidden; no visibility event would otherwise rearm idle.
            _idleTimer.Interval = IdleTimerInterval;
            _idleTimer.Start();
        }
    }

    internal async Task<bool> LoadFilePackPathsAsync(IReadOnlyList<string> paths)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || FilePack is null || Window?.IsExitInProgress == true || _viewModel.FilePack.IsStopping) return false;
        var loaded = await FilePack.AddShellPathsAsync(paths);
        if (!_disposed && !loaded) ShowWindow();
        return loaded;
    }

    private void WindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (_disposed || sender is not MainWindow current || !ReferenceEquals(current, Window)) return;
        _idleTimer.Stop();
        CancelIdleMemoryReclaim();
        _hiddenSince = 0;
        _hiddenGeneration++;
        if (!current.IsVisible)
        {
            _hiddenSince = System.Diagnostics.Stopwatch.GetTimestamp();
            _idleTimer.Interval = IdleTimerInterval;
            _idleTimer.Start();
        }
    }

    private void WindowStateChanged(object? sender, EventArgs args)
    {
        if (sender is MainWindow window && ReferenceEquals(Window, window) && window.WindowState != WindowState.Minimized)
            _lastVisibleState = window.WindowState;
    }

    private void ReleaseHiddenWindow(object? sender, EventArgs args)
    {
        // Runtime-disabled UI fixtures retain the separate page-lifetime path.
        // Production retires pages only when a single guarded collection can
        // also return their committed heap; retirement alone measured worse.
        if (Notes is null) TryReleaseHiddenViews();
        else TryQueueIdleMemoryReclaim();
    }

    internal bool TryReleaseHiddenViews()
    {
        _dispatcher.VerifyAccess();
        _idleTimer.Stop();
        var current = Window;
        if (_disposed || current is null || current.IsVisible || current.CreatedSettingsViewCount == 0) return false;
        if (current.TryReleaseDeferredViewsForIdle()) return true;
        if (current.IdleReleaseCanRetry)
        {
            _idleTimer.Interval = _idleDelay < TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : _idleDelay;
            _idleTimer.Start();
        }
        return false;
    }

    internal bool TryReleaseHiddenWindow()
    {
        _dispatcher.VerifyAccess();
        _idleTimer.Stop();
        var current = Window;
        if (_disposed || current is null || current.IsVisible) return false;
        CancelIdleMemoryReclaim();
        var previous = _releasedMainWindowHandle;
        _releasedMainWindowHandle = new WindowInteropHelper(current).Handle;
        if (current.TryReleaseForIdle(out var state)) { _uiState = state; return true; }
        _releasedMainWindowHandle = previous;
        if (ReferenceEquals(Window, current) && !current.IsVisible && current.IdleReleaseCanRetry)
        {
            // Busy work may finish without another visibility event. Retry slowly;
            // a persistent draft veto does not keep a polling timer running.
            _idleTimer.Interval = _idleDelay < TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : _idleDelay;
            _idleTimer.Start();
        }
        return false;
    }

    private void WindowClosed(object? sender, EventArgs args)
    {
        if (sender is not MainWindow closed) return;
        closed.IsVisibleChanged -= WindowVisibilityChanged;
        closed.StateChanged -= WindowStateChanged;
        closed.SourceInitialized -= MainWindowSourceInitialized;
        closed.Closed -= WindowClosed;
        if (!ReferenceEquals(Window, closed)) return;
        _idleTimer.Stop();
        CancelIdleMemoryReclaim();
        Window = null;
        if (Application.Current is { } application && ReferenceEquals(application.MainWindow, closed))
            application.MainWindow = null;
        if (!closed.IsExitPrepared) return;
        Dispose();
        if (_shutdownApplication) Application.Current?.Shutdown();
    }

    private IntPtr RuntimeMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == SingleInstance.ShowMessage)
        { handled = true; _dispatcher.BeginInvoke(ShowWindow); }
        else if ((uint)message == SingleInstance.ExitMessage)
        { handled = true; _dispatcher.BeginInvoke(RequestExit); }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelIdleMemoryReclaim();
        _idleTimer.Stop();
        _idleTimer.Tick -= ReleaseHiddenWindow;
        _viewModel.AdministratorRestartRequested -= RequestExit;
        _viewModel.Gaming.PropertyChanged -= GamingChanged;
        if (Window is { } window)
        {
            window.IsVisibleChanged -= WindowVisibilityChanged;
            window.StateChanged -= WindowStateChanged;
            window.SourceInitialized -= MainWindowSourceInitialized;
            window.Closed -= WindowClosed;
        }
        Window = null;
        WindowCreated = null;
        if (FilePack is not null) FilePack.LegacyShortcutReleased -= LegacyShortcutReleased;
        FilePack?.Dispose(); FilePack = null;
        ClipRecall?.Dispose(); ClipRecall = null;
        Notes?.Dispose(); Notes = null;
        LensShortcuts?.Dispose(); LensShortcuts = null;
        _tray?.Dispose(); _tray = null;
        if (_source is not null)
        {
            RemoveProp(_source.Handle, SingleInstance.WindowMarker);
            _source.RemoveHook(RuntimeMessage);
            _source.Dispose();
            _source = null;
        }
        _uiState = null;
        _releasedMainWindowHandle = IntPtr.Zero;
        _viewModel.Dispose();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetProp(IntPtr window, string name, IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr RemoveProp(IntPtr window, string name);
    [DllImport("user32.dll")]
    private static extern bool ChangeWindowMessageFilterEx(IntPtr window, uint message, uint action, IntPtr change);
}
