using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;

namespace DailyToolkit.Desktop.Presentation;

public sealed class GamingViewModel : ObservableObject, IDisposable
{
    private readonly ScreenLensSession _session = new();
    private CaptureMonitor? _monitor;
    private double _zoom = 2, _width = 640, _height = 384, _sharpening = 0.35;
    private bool _favorite;
    private int _fps;
    private int _positionResetVersion;
    private string _status = "未开启";
    private readonly GamingPreferencesStore _preferencesStore;
    private GamingPreferences _preferences;

    public GamingViewModel(GamingPreferencesStore? preferencesStore = null)
    {
        _preferencesStore = preferencesStore ?? new();
        _preferences = _preferencesStore.Load();
        _session.SetMovement(IsMovableMode);
        _session.SetWheelZoom(WheelZoomEnabled);
        _session.StateChanged += RefreshState;
        _session.VisibilityChanged += () => Notify(nameof(IsVisible));
        _session.StatusChanged += value => Status=value;
        _session.ZoomRequested += AdjustZoom;
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
    public double MaximumZoom => LensLayout.MaximumZoom;
    public double Zoom { get => _zoom; set { if (double.IsFinite(value) && Set(ref _zoom,Math.Clamp(value,1,MaximumZoom))) { Notify(nameof(ZoomText)); _session.UpdatePicture(Zoom,Sharpening); } } }
    public double FrameWidth { get => _width; set { if (double.IsFinite(value) && Set(ref _width,Math.Round(Math.Clamp(value,160,1600)))) Notify(nameof(FrameSizeText)); } }
    public double FrameHeight { get => _height; set { if (double.IsFinite(value) && Set(ref _height,Math.Round(Math.Clamp(value,120,1200)))) Notify(nameof(FrameSizeText)); } }
    public double Sharpening { get => _sharpening; set { if (double.IsFinite(value) && Set(ref _sharpening,Math.Clamp(value,0,1))) _session.UpdatePicture(Zoom,Sharpening); } }
    public int FrameRate { get => _fps; set => Set(ref _fps,FrameRates.Any(rate => rate.Value == value) ? value : 0); }
    public string FrameRateText => FrameRates.First(rate => rate.Value == FrameRate).Label;
    public void AdjustZoom(int wheelDelta) { if (WheelZoomEnabled) Zoom += wheelDelta/120d*0.25; }
    public bool WheelZoomEnabled
    {
        get => _preferences.WheelZoomEnabled;
        set
        {
            if (value == WheelZoomEnabled) return;
            SavePreferences(_preferences with { WheelZoomEnabled=value });
            Notify(nameof(WheelZoomEnabled)); Notify(nameof(MovementHint));
            _session.SetWheelZoom(value);
        }
    }
    public bool IsActive => _session.IsActive;
    internal PixelBounds? ActiveBounds => _session.ActiveBounds;
    internal LensPointerController? PointerForDiagnostics => _session.PointerForDiagnostics;
    internal bool VerticalGuide => _session.VerticalGuide;
    internal bool HorizontalGuide => _session.HorizontalGuide;
    internal IntPtr LensHandle => _session.LensHandle;
    internal LensDesktopGuides? GuidesForDiagnostics => _session.GuidesForDiagnostics;
    internal LensCapture? CaptureForDiagnostics => _session.CaptureForDiagnostics;
    internal bool HasCaptureResources => _session.HasCaptureResources;
    public bool IsRequestedVisible => IsActive;
    public bool IsVisible => _session.IsVisible;
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
            _session.SetMovement(IsMovableMode);
        }
    }
    public bool IsFixedMode { get => MovementMode == LensMovementMode.Fixed; set { if (value) MovementMode=LensMovementMode.Fixed; } }
    public bool IsMovableMode { get => MovementMode == LensMovementMode.Movable; set { if (value) MovementMode=LensMovementMode.Movable; } }
    public string MovementHint => (IsFixedMode ? "锁住当前位置，点击可操作下面的窗口。"
        : "按住放大画面拖动整个浮窗。拖动时显示屏幕中心辅助线，靠近时吸附；摆好位置后可切回固定。") +
        (WheelZoomEnabled ? "框内滚轮调倍率（1–10×）。" : "滚轮调节已关闭，可用上方滑块调整倍率。");
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
        _session.Start(monitor,(int)Math.Round(FrameWidth),(int)Math.Round(FrameHeight),Zoom,Sharpening,FrameRate);
    }

    public void ResetDefaults()
    {
        Stop();
        Zoom=2; FrameWidth=640; FrameHeight=384; Sharpening=0.35; FrameRate=0;
        SelectedMonitor=Monitors.FirstOrDefault();
        MovementMode=LensMovementMode.Fixed; WheelZoomEnabled=true;
        _session.ResetPosition(SelectedMonitor);
        _positionResetVersion++;
        Notify(nameof(PositionResetVersion));
        Status="已恢复默认画面设置，快捷键、按住／切换模式和收藏保留。";
    }

    public void Toggle()
    {
        if (IsActive) Stop(); else Start();
    }
    public void Stop() => _session.Stop();
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
