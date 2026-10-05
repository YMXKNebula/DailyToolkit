using System.Windows;
using DailyUSE.Core.Gaming;
using DailyUSE.Desktop.Gaming;
using Windows.Graphics.Capture;

namespace DailyUSE.Desktop.Presentation;

public sealed class GamingViewModel : ObservableObject, IDisposable
{
    private LensNativeWindow? _window;
    private LensGpuRenderer? _renderer;
    private LensCapture? _capture;
    private CaptureMonitor? _monitor;
    private double _zoom = 2, _width = 640, _sharpening = 0.35;
    private bool _followMouse, _active, _hidden;
    private int _fps = 60, _generation;
    private string _status = "未开启";
    private readonly GamingPreferencesStore _preferencesStore;
    private GamingPreferences _preferences;
    private System.Windows.Threading.DispatcherTimer? _startupTimer;

    public GamingViewModel(GamingPreferencesStore? preferencesStore = null)
    {
        _preferencesStore = preferencesStore ?? new();
        _preferences = _preferencesStore.Load();
        Monitors = LensNativeWindow.Monitors();
        _monitor = Monitors.FirstOrDefault();
        StartCommand = new(_ => Start(), () => !IsActive && SelectedMonitor is not null);
        StopCommand = new(_ => Stop(), () => IsActive);
        ToggleCommand = new(_ => Toggle(), () => IsActive);
    }

    public IReadOnlyList<CaptureMonitor> Monitors { get; private set; }
    public void RefreshMonitors()
    {
        if (IsActive) return;
        var selected=_monitor?.Handle;
        Monitors=LensNativeWindow.Monitors();
        Notify(nameof(Monitors));
        SelectedMonitor=Monitors.FirstOrDefault(m => m.Handle == selected) ?? Monitors.FirstOrDefault();
    }
    public IReadOnlyList<int> FrameRates { get; } = new[] { 30,60 };
    public CaptureMonitor? SelectedMonitor { get => _monitor; set { if (Set(ref _monitor,value)) StartCommand.Refresh(); } }
    public double Zoom { get => _zoom; set { if (double.IsFinite(value) && Set(ref _zoom,Math.Clamp(value,1.5,4))) Notify(nameof(ZoomText)); } }
    public double FrameWidth { get => _width; set { if (double.IsFinite(value) && Set(ref _width,Math.Clamp(value,320,1000))) Notify(nameof(FrameSizeText)); } }
    public double Sharpening { get => _sharpening; set { if (double.IsFinite(value)) Set(ref _sharpening,Math.Clamp(value,0,1)); } }
    public int FrameRate { get => _fps; set => Set(ref _fps,value == 30 ? 30 : 60); }
    public bool FollowMouse { get => _followMouse; set => Set(ref _followMouse,value); }
    public bool IsActive => _active;
    public bool CanConfigure => !IsActive;
    public string ZoomText => $"{Zoom:0.#}×";
    public string FrameSizeText => $"{Math.Round(FrameWidth)} × {Math.Round(FrameWidth*0.6)} 像素";
    public string Status { get => _status; private set => Set(ref _status,value); }
    public string ToggleText => _hidden ? "显示放大框" : "临时隐藏";
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public KeyboardShortcut? ToggleShortcut => _preferences.ToggleShortcut;
    public KeyboardShortcut? CloseShortcut => _preferences.CloseShortcut;
    public string ToggleShortcutText => ToggleShortcut?.Name ?? "无";
    public string CloseShortcutText => CloseShortcut?.Name ?? "无";
    public string ShortcutHint => $"显示 / 临时隐藏：{ToggleShortcutText}\n关闭放大框：{CloseShortcutText}";
    public bool SetShortcut(bool toggle, KeyboardShortcut? shortcut)
    {
        var preferences = toggle ? _preferences with { ToggleShortcut=shortcut } : _preferences with { CloseShortcut=shortcut };
        if (!preferences.IsValid) { Status="两个操作不能使用相同快捷键，请换一个按键。"; return false; }
        _preferences = preferences;
        if (!_preferencesStore.Save(preferences)) Status="快捷键已在本次运行中修改，但没有保存成功。";
        foreach (var name in new[] { nameof(ToggleShortcut),nameof(CloseShortcut),nameof(ToggleShortcutText),nameof(CloseShortcutText),nameof(ShortcutHint) }) Notify(name);
        return true;
    }

    public void Start()
    {
        if (IsActive) return;
        RefreshMonitors();
        if (SelectedMonitor is not { } monitor) { Status="未找到可用的显示器。"; return; }
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) { Status = "当前 Windows 图形环境不支持屏幕捕获。"; return; }
            var width = (int)Math.Round(FrameWidth);
            var height = (int)Math.Round(FrameWidth*0.6);
            var layout = LensLayout.Calculate(monitor.Bounds,width,height,Zoom,
                monitor.Bounds.Left+monitor.Bounds.Width/2,monitor.Bounds.Top+monitor.Bounds.Height/2);
            _window = new(layout.Output);
            _renderer = new(_window.Handle,layout.Output.Width,layout.Output.Height);
            var item = CaptureInterop.ForMonitor(monitor.Handle);
            var generation = ++_generation;
            SourceArea MovingSource()
            {
                var cursor = LensNativeWindow.Cursor();
                var moving = LensLayout.Calculate(monitor.Bounds,width,height,Zoom,cursor.X,cursor.Y);
                _window?.Move(moving.Output);
                return moving.Source;
            }
            _capture = new(_renderer,item,layout.Source,Sharpening,FrameRate,FollowMouse ? MovingSource : null);
            _capture.FirstFrame += () => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (generation != _generation || !IsActive) return;
                _startupTimer?.Stop();
                if (!_hidden) _window?.Show();
                Status = $"已开启 · {ZoomText} · {(FollowMouse ? "跟随鼠标" : "屏幕中央")}";
            });
            _capture.Failed += reason => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (generation != _generation) return;
                Stop(); Status = reason;
            });
            _active = true; _hidden = false;
            RefreshState();
            Status = "正在读取屏幕画面…";
            _capture.Start();
            _startupTimer = new System.Windows.Threading.DispatcherTimer { Interval=TimeSpan.FromSeconds(5) };
            _startupTimer.Tick += (_,_) =>
            {
                _startupTimer?.Stop();
                if (generation != _generation || !IsActive || _capture!.FramesRendered > 0) return;
                Stop(); Status="暂时没有取得画面，请确认 Windows 允许捕获，并使用窗口或无边框模式。";
            };
            _startupTimer.Start();
        }
        catch (Exception)
        {
            Stop();
            Status = "没有开启成功。请使用窗口或无边框模式，并确认 Windows 允许屏幕捕获。";
        }
    }

    public void Toggle()
    {
        if (!IsActive) return;
        _hidden = !_hidden;
        if (_capture is not null) _capture.Paused = _hidden;
        if (_hidden) _window?.Hide(); else _window?.Show();
        Status = _hidden ? "已临时隐藏" : $"已开启 · {ZoomText}";
        Notify(nameof(ToggleText));
    }
    public void Stop()
    {
        _generation++;
        _startupTimer?.Stop(); _startupTimer=null;
        _window?.Hide();
        _capture?.Dispose(); _capture = null;
        _renderer?.Dispose(); _renderer = null;
        _window?.Dispose(); _window = null;
        _active = false; _hidden = false;
        Status = "未开启";
        RefreshState();
    }
    private void RefreshState()
    {
        foreach (var name in new[] { nameof(IsActive),nameof(CanConfigure),nameof(ToggleText) }) Notify(name);
        StartCommand.Refresh(); StopCommand.Refresh(); ToggleCommand.Refresh();
    }
    public void Dispose() => Stop();
}
