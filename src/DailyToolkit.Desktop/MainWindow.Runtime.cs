using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DailyToolkit.Desktop.Runtime;
namespace DailyToolkit.Desktop;

public partial class MainWindow
{
    private TrayIcon? _tray;
    private HwndSource? _runtimeSource;
    private bool _exitRequested,_initializedRuntime;
    private bool _computerInitialized;
    private bool _trackActivity;
    internal bool TrackActivity { get => _trackActivity; set { _trackActivity=value; UpdateLocalPolling(); } }
    internal bool IsLocalPolling => _clockTimer.IsEnabled;
    private WindowState _lastVisibleState=WindowState.Normal;
    internal bool TrayAvailable => _tray?.Available == true;
    internal void InitializeRuntime(HwndSource source)
    {
        _runtimeSource=source; source.AddHook(RuntimeMessage);
        SetProp(source.Handle,SingleInstance.WindowMarker,new IntPtr(1));
        ChangeWindowMessageFilterEx(source.Handle,SingleInstance.ShowMessage,1,IntPtr.Zero);
        ChangeWindowMessageFilterEx(source.Handle,SingleInstance.ExitMessage,1,IntPtr.Zero);
        _tray=new(source);
        _tray.ShowRequested += ShowFromTray; _tray.ExitRequested += RequestExit;
        _tray.SettingsRequested += () => { ShowFromTray(); if (!_viewModel.IsSettings) ToggleSettings(SettingsButton,new RoutedEventArgs()); };
        _viewModel.AdministratorRestartRequested += RequestExit;
        _viewModel.RefreshStartupRegistration();
    }
    internal void StartHidden()
    {
        new WindowInteropHelper(this).EnsureHandle();
        // Explorer may not have created its tray yet during logon. The icon retries;
        // a silent launch never flashes the main window while waiting for it.
        StartLocalRuntime();
    }
    private void StartLocalRuntime()
    {
        if (_initializedRuntime) return;
        _initializedRuntime=true; UpdateLocalPolling();
        Dispatcher.BeginInvoke(new Action(UpdateNavigationBridge));
    }
    private void UpdateLocalPolling()
    {
        if (_clockTimer is null || _viewModel is null) return;
        var visible=IsVisible && WindowState != WindowState.Minimized && (!_trackActivity || IsActive);
        LensPhotoPreview.SetHostActive(visible);
        if (!_initializedRuntime || !visible || !_viewModel.IsHome) { _clockTimer.Stop(); return; }
        _viewModel.UpdateClock();
        if (!_computerInitialized) { _computerInitialized=true; _= _viewModel.InitializeAsync(); }
        else if (!_clockTimer.IsEnabled) _viewModel.QueueLocalRefresh();
        _clockTimer.Start();
    }
    internal void ShowFromTray()
    {
        if (_exitRequested || _closing) return;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState=_lastVisibleState;
        Activate();
    }
    private bool HideToTray()
    {
        if (_tray?.Available != true) return false;
        if (WindowState != WindowState.Minimized) _lastVisibleState=WindowState;
        FinishPageTransition(); FinishNavigationTransition(); _shortcuts?.Suspend(false); _notes?.Suspend(false); Hide(); return true;
    }
    internal void RequestExit() { _exitRequested=true; Close(); }
    private IntPtr RuntimeMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if ((uint)message == SingleInstance.ShowMessage) { handled=true; Dispatcher.BeginInvoke(ShowFromTray); }
        else if ((uint)message == SingleInstance.ExitMessage) { handled=true; Dispatcher.BeginInvoke(RequestExit); }
        return IntPtr.Zero;
    }
    private void DisposeRuntime()
    {
        _viewModel.AdministratorRestartRequested -= RequestExit;
        if (_runtimeSource is not null) { RemoveProp(_runtimeSource.Handle,SingleInstance.WindowMarker); _runtimeSource.RemoveHook(RuntimeMessage); }
        _tray?.Dispose(); _tray=null;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool SetProp(IntPtr window,string name,IntPtr value);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr RemoveProp(IntPtr window,string name);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr window,uint message,uint action,IntPtr change);
}
