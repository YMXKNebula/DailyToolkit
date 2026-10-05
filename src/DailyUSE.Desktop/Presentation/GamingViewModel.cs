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
    private LensPointerController? _pointer;
    private LensLayout? _layout;
    private CaptureMonitor? _activeMonitor;
    private IntPtr _positionMonitor;
    private double _positionX=0.5,_positionY=0.5;
    private int _centerX,_centerY;
    private CaptureMonitor? _monitor;
    private double _zoom = 2, _width = 640, _height = 384, _sharpening = 0.35;
    private bool _active;
    private bool _favorite;
    private int _fps, _generation;
    private int _positionResetVersion;
    private bool _verticalGuide,_horizontalGuide;
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
        ResetDefaultsCommand = new(_ => ResetDefaults());
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
    public IReadOnlyList<LensFrameRate> FrameRates { get; } = new LensFrameRate[] { new(0,"跟随画面"),new(30,"30 帧/秒"),new(60,"60 帧/秒"),new(120,"120 帧/秒"),new(144,"144 帧/秒"),new(240,"240 帧/秒") };
    public CaptureMonitor? SelectedMonitor { get => _monitor; set { if (Set(ref _monitor,value)) { StartCommand.Refresh(); ToggleCommand.Refresh(); } } }
    public double Zoom { get => _zoom; set { if (double.IsFinite(value) && Set(ref _zoom,Math.Clamp(value,1,8))) { Notify(nameof(ZoomText)); Reposition(); } } }
    public double FrameWidth { get => _width; set { if (double.IsFinite(value) && Set(ref _width,Math.Round(Math.Clamp(value,160,1600)))) Notify(nameof(FrameSizeText)); } }
    public double FrameHeight { get => _height; set { if (double.IsFinite(value) && Set(ref _height,Math.Round(Math.Clamp(value,120,1200)))) Notify(nameof(FrameSizeText)); } }
    public double Sharpening { get => _sharpening; set { if (double.IsFinite(value)) Set(ref _sharpening,Math.Clamp(value,0,1)); } }
    public int FrameRate { get => _fps; set => Set(ref _fps,FrameRates.Any(rate => rate.Value == value) ? value : 0); }
    public string FrameRateText => FrameRates.First(rate => rate.Value == FrameRate).Label;
    public void AdjustZoom(int wheelDelta) => Zoom += wheelDelta/120d*0.25;
    public bool IsActive => _active;
    internal PixelBounds? ActiveBounds => _layout?.Output;
    internal LensPointerController? PointerForDiagnostics => _pointer;
    internal bool VerticalGuide => _verticalGuide;
    internal bool HorizontalGuide => _horizontalGuide;
    internal IntPtr LensHandle => _window?.Handle ?? IntPtr.Zero;
    internal bool HasCaptureResources => _window is not null || _renderer is not null || _capture is not null || _pointer is not null || _startupTimer is not null;
    public bool IsRequestedVisible => IsActive;
    public bool IsVisible => IsRequestedVisible && _capture is { FramesRendered: > 0 };
    public bool CanConfigure => !IsActive;
    public string ZoomText => $"{Zoom:0.##}×";
    public string FrameSizeText => $"{FrameWidth:0} × {FrameHeight:0} 像素";
    public string Status { get => _status; private set => Set(ref _status,value); }
    public string ToggleText => IsActive ? "关闭放大框" : "开启放大框";
    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ToggleCommand { get; }
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand ResetDefaultsCommand { get; }
    public int PositionResetVersion => _positionResetVersion;
    public LensMovementMode MovementMode
    {
        get => _preferences.MovementMode;
        set
        {
            if (value == MovementMode || value is not (LensMovementMode.Movable or LensMovementMode.Fixed)) return;
            SavePreferences(_preferences with { MovementMode=value });
            Notify(nameof(MovementMode)); Notify(nameof(IsFixedMode)); Notify(nameof(IsMovableMode)); Notify(nameof(MovementHint));
            if (IsFixedMode) { _pointer?.CancelDrag(); ClearGuides(); }
            if (IsVisible) UpdateActiveStatus();
        }
    }
    public bool IsFixedMode { get => MovementMode == LensMovementMode.Fixed; set { if (value) MovementMode=LensMovementMode.Fixed; } }
    public bool IsMovableMode { get => MovementMode == LensMovementMode.Movable; set { if (value) MovementMode=LensMovementMode.Movable; } }
    public string MovementHint => IsFixedMode
        ? "锁住当前位置。框内滚轮调倍率（1–8×），点击可操作下面的窗口。"
        : "按住放大画面中的任意位置拖动，靠近屏幕中心时吸附并显示辅助线。框内滚轮调倍率（1–8×）；摆好位置后切回固定，可操作下面的窗口。";
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
            Stop();
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
        Stop();
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
            var height = (int)Math.Round(FrameHeight);
            var centerX=monitor.Bounds.Left+(int)Math.Round(monitor.Bounds.Width*(_positionMonitor == monitor.Handle ? _positionX : 0.5));
            var centerY=monitor.Bounds.Top+(int)Math.Round(monitor.Bounds.Height*(_positionMonitor == monitor.Handle ? _positionY : 0.5));
            var layout = LensLayout.Calculate(monitor.Bounds,width,height,Zoom,centerX,centerY);
            _activeMonitor=monitor;
            _centerX=layout.Output.Left+layout.Output.Width/2;
            _centerY=layout.Output.Top+layout.Output.Height/2;
            // Keep the sampled area aligned with the frame when a changed size clamps its position.
            _layout=layout=LensLayout.Calculate(monitor.Bounds,width,height,Zoom,_centerX,_centerY);
            _window = new(layout.Output);
            _renderer = new(_window.Handle,layout.Output.Width,layout.Output.Height);
            var item = CaptureInterop.ForMonitor(monitor.Handle);
            var generation = ++_generation;
            _capture = new(_renderer,item,layout.Source,Sharpening,FrameRate);
            _pointer = new(() => IsVisible ? _layout?.Output : null,MoveFrame,AdjustZoom,
                canMove:() => IsMovableMode,dragEnded:ClearGuides);
            _capture.FirstFrame += () => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (generation != _generation || !IsActive) return;
                _startupTimer?.Stop();
                _window?.Show(); UpdateActiveStatus();
                Notify(nameof(IsVisible));
            });
            _capture.Failed += reason => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (generation != _generation) return;
                Stop(); Status = reason;
            });
            _active = true;
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

    private void MoveFrame(int left,int top)
    {
        if (_layout is null || _activeMonitor is null || !IsMovableMode) return;
        var bounds=_activeMonitor.Bounds;
        var placement=LensPlacement.Snap(bounds,_layout.Output.Width,_layout.Output.Height,left,top);
        _centerX=placement.Bounds.Left+placement.Bounds.Width/2;
        _centerY=placement.Bounds.Top+placement.Bounds.Height/2;
        _verticalGuide=placement.VerticalGuide; _horizontalGuide=placement.HorizontalGuide;
        _positionMonitor=_activeMonitor.Handle;
        _positionX=(_centerX-bounds.Left)/(double)bounds.Width;
        _positionY=(_centerY-bounds.Top)/(double)bounds.Height;
        Reposition();
    }

    private void CenterFrame()
    {
        _pointer?.CancelDrag();
        _verticalGuide=_horizontalGuide=false;
        var monitor=_activeMonitor ?? SelectedMonitor;
        _positionMonitor=monitor?.Handle ?? IntPtr.Zero;
        _positionX=_positionY=0.5;
        if (monitor is not null)
        {
            _centerX=monitor.Bounds.Left+monitor.Bounds.Width/2;
            _centerY=monitor.Bounds.Top+monitor.Bounds.Height/2;
        }
        _positionResetVersion++;
        Notify(nameof(PositionResetVersion));
        Reposition();
    }

    public void ResetDefaults()
    {
        Stop();
        Zoom=2; FrameWidth=640; FrameHeight=384; Sharpening=0.35; FrameRate=0;
        SelectedMonitor=Monitors.FirstOrDefault();
        MovementMode=LensMovementMode.Fixed;
        CenterFrame();
        Status="已恢复默认画面设置，快捷键、按住／切换模式和收藏保留。";
    }

    private void ClearGuides()
    {
        if (!_verticalGuide && !_horizontalGuide) return;
        _verticalGuide=_horizontalGuide=false;
        Reposition();
    }

    private void UpdateActiveStatus() => Status=$"已开启 · {ZoomText} · " +
        (IsFixedMode ? "固定当前位置，框内滚轮调倍率" : "按住放大画面拖动，框内滚轮调倍率");

    private void Reposition()
    {
        if (_layout is null || _activeMonitor is null || !IsActive) return;
        _layout=LensLayout.Calculate(_activeMonitor.Bounds,_layout.Output.Width,_layout.Output.Height,Zoom,_centerX,_centerY);
        _window?.Move(_layout.Output);
        _capture?.UpdateSource(_layout.Source,Sharpening,_verticalGuide,_horizontalGuide);
        UpdateActiveStatus();
    }

    public void Toggle()
    {
        if (IsActive) Stop(); else Start();
    }
    public void Stop()
    {
        _generation++;
        _pointer?.Dispose(); _pointer=null;
        _startupTimer?.Stop(); _startupTimer=null;
        _window?.Hide();
        _capture?.Dispose(); _capture = null;
        _renderer?.Dispose(); _renderer = null;
        _window?.Dispose(); _window = null;
        _layout=null; _activeMonitor=null;
        _verticalGuide=_horizontalGuide=false;
        _active = false;
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

public sealed record LensFrameRate(int Value,string Label);
