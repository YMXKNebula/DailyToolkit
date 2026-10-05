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
    private bool _favorite;
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
        ToggleCommand = new(_ => Toggle(), () => IsActive || SelectedMonitor is not null);
        ToggleFavoriteCommand = new(_ => IsFavorite = !IsFavorite);
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
    public CaptureMonitor? SelectedMonitor { get => _monitor; set { if (Set(ref _monitor,value)) { StartCommand.Refresh(); ToggleCommand.Refresh(); } } }
    public double Zoom { get => _zoom; set { if (double.IsFinite(value) && Set(ref _zoom,Math.Clamp(value,1.5,4))) Notify(nameof(ZoomText)); } }
    public double FrameWidth { get => _width; set { if (double.IsFinite(value) && Set(ref _width,Math.Clamp(value,320,1000))) Notify(nameof(FrameSizeText)); } }
    public double Sharpening { get => _sharpening; set { if (double.IsFinite(value)) Set(ref _sharpening,Math.Clamp(value,0,1)); } }
    public int FrameRate { get => _fps; set => Set(ref _fps,value == 30 ? 30 : 60); }
    public bool FollowMouse { get => _followMouse; set => Set(ref _followMouse,value); }
    public bool IsActive => _active;
    public bool IsRequestedVisible => _active && !_hidden;
    public bool IsVisible => IsRequestedVisible && _capture is { FramesRendered: > 0 };
    public bool CanConfigure => !IsActive;
    public string ZoomText => $"{Zoom:0.#}×";
    public string FrameSizeText => $"{Math.Round(FrameWidth)} × {Math.Round(FrameWidth*0.6)} 像素";
    public string Status { get => _status; private set => Set(ref _status,value); }
    public string ToggleText => IsRequestedVisible ? "隐藏放大框" : "显示放大框";
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public bool IsFavorite
    {
        get => _favorite;
        set { if (Set(ref _favorite,value)) { Notify(nameof(FavoriteSymbol)); Notify(nameof(FavoriteHint)); } }
    }
    public string FavoriteSymbol => IsFavorite ? "★" : "☆";
    public string FavoriteHint => IsFavorite ? "取消收藏" : "加入收藏夹";
    public KeyboardShortcut? ToggleShortcut => _preferences.ToggleShortcut;
    public string ToggleShortcutText => ToggleShortcut?.Name ?? "无";
    public LensActivationMode ActivationMode
    {
        get => _preferences.ActivationMode;
        set
        {
            if (value == ActivationMode || value is not (LensActivationMode.Toggle or LensActivationMode.Hold)) return;
            Hide();
            SavePreferences(_preferences with { ActivationMode=value });
            Notify(nameof(ActivationMode)); Notify(nameof(IsHoldMode)); Notify(nameof(IsToggleMode));
        }
    }
    public bool IsHoldMode { get => ActivationMode == LensActivationMode.Hold; set { if (value) ActivationMode=LensActivationMode.Hold; } }
    public bool IsToggleMode { get => ActivationMode == LensActivationMode.Toggle; set { if (value) ActivationMode=LensActivationMode.Toggle; } }
    public bool SetShortcut(KeyboardShortcut? shortcut)
    {
        var preferences = _preferences with { ToggleShortcut=shortcut };
        if (!preferences.IsValid) { Status="这个按键不能用作快捷键。"; return false; }
        Hide();
        SavePreferences(preferences);
        Notify(nameof(ToggleShortcut)); Notify(nameof(ToggleShortcutText));
        return true;
    }
    private void SavePreferences(GamingPreferences preferences)
    {
        _preferences = preferences;
        if (!_preferencesStore.Save(preferences)) Status="设置已在本次运行中修改，但没有保存成功。";
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
                if (!_hidden) { _window?.Show(); Status = $"已显示 · {ZoomText} · {(FollowMouse ? "跟随鼠标" : "屏幕中央")}"; }
                Notify(nameof(IsVisible));
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
                if (generation != _generation || !IsRequestedVisible || _capture!.FramesRendered > 0) return;
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
        if (IsRequestedVisible) Hide(); else Show();
    }
    public void Show()
    {
        if (!IsActive) { Start(); return; }
        _hidden = false;
        if (_capture is not null) _capture.Paused = false;
        if (_capture is { FramesRendered: > 0 }) { _window?.Show(); Status=$"已显示 · {ZoomText}"; }
        else { Status="正在读取屏幕画面…"; _startupTimer?.Start(); }
        RefreshVisibility();
    }
    public void Hide()
    {
        if (!IsActive) return;
        _hidden = true;
        if (_capture is not null) _capture.Paused = true;
        _startupTimer?.Stop();
        _window?.Hide();
        Status="已隐藏";
        RefreshVisibility();
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
        foreach (var name in new[] { nameof(IsActive),nameof(CanConfigure) }) Notify(name);
        RefreshVisibility();
        StartCommand.Refresh(); StopCommand.Refresh(); ToggleCommand.Refresh();
    }
    private void RefreshVisibility()
    {
        Notify(nameof(IsRequestedVisible)); Notify(nameof(IsVisible)); Notify(nameof(ToggleText));
    }
    public void Dispose() => Stop();
}
