using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;

namespace DailyToolkit.Desktop.Presentation;

public sealed class GamingViewModel : ObservableObject, IDisposable
{
    private readonly ScreenLensSession _session = new();
    private CaptureMonitor? _monitor;
    private double _zoom = 2, _width = 640, _height = 384;
    private bool _favorite;
    private int _fps;
    private int _positionResetVersion;
    private string _status = "未开启";
    private readonly GamingPreferencesStore _preferencesStore;
    private GamingPreferences _preferences;
    private readonly LensWheelAccumulator _wheel = new();

    public GamingViewModel(GamingPreferencesStore? preferencesStore = null)
    {
        _preferencesStore = preferencesStore ?? new();
        _preferences = _preferencesStore.Load();
        _width=_preferences.FrameWidth; _height=_preferences.FrameHeight;
        _zoom=_preferences.Zoom;
        _session.SetImageMode(_preferences.LensImageMode);
        _session.SetMovement(IsMovableMode);
        _session.SetWheelZoom(WheelZoomEnabled);
        _session.SetBorderColor(BorderColorRgb);
        _session.StateChanged += RefreshState;
        _session.VisibilityChanged += () => Notify(nameof(IsVisible));
        _session.StatusChanged += value => Status=value;
        _session.ZoomRequested += AdjustZoom;
        _session.MonitorChanged += monitor => SelectedMonitor=monitor;
        Monitors = LensNativeWindow.Monitors();
        _monitor = MonitorUnderCursor();
        StartCommand = new(_ => Start(), () => IsEnabled && !IsActive && SelectedMonitor is not null);
        StopCommand = new(_ => Stop(), () => IsActive);
        ToggleCommand = new(_ => Toggle(), () => IsEnabled && (IsActive || SelectedMonitor is not null));
        ToggleFavoriteCommand = new(_ => IsFavorite = !IsFavorite);
        ResetDefaultsCommand = new(_ => ResetDefaults());
    }

    public IReadOnlyList<CaptureMonitor> Monitors { get; private set; }
    public void RefreshMonitors()
    {
        if (IsActive) return;
        Monitors=LensNativeWindow.Monitors();
        Notify(nameof(Monitors));
        SelectedMonitor=MonitorUnderCursor();
    }
    private CaptureMonitor? MonitorUnderCursor()
    {
        if (Monitors.Count == 0) return null;
        var cursor=LensNativeWindow.Cursor();
        return Monitors[LensPlacement.MonitorAt(Monitors.Select(m => m.Bounds).ToArray(),cursor.X,cursor.Y)];
    }
    public IReadOnlyList<LensFrameRate> FrameRates { get; } = new LensFrameRate[] { new(0,"跟随画面"),new(30,"30 帧/秒"),new(60,"60 帧/秒"),new(120,"120 帧/秒"),new(144,"144 帧/秒"),new(240,"240 帧/秒") };
    public CaptureMonitor? SelectedMonitor { get => _monitor; private set { if (Set(ref _monitor,value)) { NotifyFrameSize(); StartCommand.Refresh(); ToggleCommand.Refresh(); } } }
    public IReadOnlyList<LensSizeOption> FrameSizePresets { get; } = [new(LensFrameSizePreset.Custom,"自定义"),
        new(LensFrameSizePreset.Small,"小 · 屏幕宽高的 20%"),new(LensFrameSizePreset.Medium,"中 · 屏幕宽高的 30%"),
        new(LensFrameSizePreset.Large,"大 · 屏幕宽高的 40%")];
    public LensFrameSizePreset FrameSizePreset
    {
        get => _preferences.FrameSizePreset;
        set
        {
            if (IsActive || value == FrameSizePreset || !Enum.IsDefined(value)) return;
            SavePreferences(_preferences with { FrameSizePreset=value });
            Notify(); Notify(nameof(IsCustomFrameSize)); Notify(nameof(IsPresetFrameSize)); NotifyFrameSize();
        }
    }
    public bool IsCustomFrameSize => FrameSizePreset == LensFrameSizePreset.Custom;
    public bool IsPresetFrameSize => !IsCustomFrameSize;
    public IReadOnlyList<LensAspectOption> AspectRatios { get; } = [new(LensAspectRatio.Screen,"跟随屏幕比例"),
        new(LensAspectRatio.Square,"1:1"),new(LensAspectRatio.Wide,"16:9"),new(LensAspectRatio.Standard,"4:3"),new(LensAspectRatio.Ultrawide,"21:9")];
    public LensAspectRatio AspectRatio
    {
        get => _preferences.AspectRatio;
        set
        {
            if (IsActive || value == AspectRatio || !Enum.IsDefined(value)) return;
            SavePreferences(_preferences with { AspectRatio=value }); Notify(); NotifyFrameSize();
        }
    }
    private (int Width,int Height) EffectiveFrameSize => SelectedMonitor is { } monitor
        ? LensFrameSize.Resolve(monitor.Bounds,(int)_width,(int)_height,FrameSizePreset,AspectRatio) : ((int)_width,(int)_height);
    public int EffectiveFrameWidth => EffectiveFrameSize.Width;
    public int EffectiveFrameHeight => EffectiveFrameSize.Height;
    private void NotifyFrameSize() { Notify(nameof(EffectiveFrameWidth)); Notify(nameof(EffectiveFrameHeight)); Notify(nameof(FrameSizeText)); }
    public bool IsEnabled
    {
        get => _preferences.IsEnabled;
        set
        {
            if (value == IsEnabled) return;
            if (!value) Stop();
            SavePreferences(_preferences with { IsEnabled=value }); Notify(); Notify(nameof(IsDisabled));
            StartCommand.Refresh(); ToggleCommand.Refresh();
        }
    }
    public bool IsDisabled => !IsEnabled;
    public double MaximumZoom => LensLayout.MaximumZoom;
    public double Zoom { get => _zoom; set { if (Set(ref _zoom,LensZoom.Normalize(value))) { SavePreferences(_preferences with { Zoom=_zoom }); Notify(nameof(ZoomText)); _session.UpdatePicture(Zoom,Sharpening); } } }
    public double FrameWidth { get => _width; set { if (double.IsFinite(value) && Set(ref _width,Math.Round(Math.Clamp(value,160,1600)))) { SavePreferences(_preferences with { FrameWidth=(int)_width }); NotifyFrameSize(); } } }
    public double FrameHeight { get => _height; set { if (double.IsFinite(value) && Set(ref _height,Math.Round(Math.Clamp(value,120,1200)))) { SavePreferences(_preferences with { FrameHeight=(int)_height }); NotifyFrameSize(); } } }
    internal double Sharpening => LensImageSettings.GetSharpening(ImageMode);
    public IReadOnlyList<LensImageOption> ImageModes { get; } = [new(LensImageMode.Performance,"性能"),
        new(LensImageMode.Clear,"清晰（推荐）"),new(LensImageMode.HighQuality,"质量"),new(LensImageMode.Pixel,"像素")];
    public LensImageMode ImageMode
    {
        get => _preferences.LensImageMode;
        set
        {
            var mode=LensImageSettings.NormalizeMode(value);
            if (mode != ImageMode)
            {
                SavePreferences(_preferences with { LensImageMode=mode }); Notify(); Notify(nameof(Sharpening));
                _session.SetImageMode(mode);
                _session.UpdatePicture(Zoom,Sharpening);
            }
            if (value == LensImageMode.AIEnhanced) Status="AI 增强当前不可用，已切换至质量模式。";
        }
    }
    public int FrameRate { get => _fps; set => Set(ref _fps,FrameRates.Any(rate => rate.Value == value) ? value : 0); }
    public string FrameRateText => FrameRates.First(rate => rate.Value == FrameRate).Label;
    public void AdjustZoom(int wheelDelta)
    {
        if (!WheelZoomEnabled) return;
        var ticks=_wheel.Consume(wheelDelta);
        if (ticks == 0) return;
        Zoom += ticks*LensZoom.Step;
        _session.ShowZoomHud(Zoom);
    }
    public bool WheelZoomEnabled
    {
        get => _preferences.WheelZoomEnabled;
        set
        {
            if (value == WheelZoomEnabled) return;
            SavePreferences(_preferences with { WheelZoomEnabled=value });
            _wheel.Reset();
            Notify(nameof(WheelZoomEnabled)); Notify(nameof(MovementHint));
            _session.SetWheelZoom(value);
        }
    }
    public bool IsActive => _session.IsActive;
    public string BorderColor
    {
        get => _preferences.BorderColor;
        set
        {
            if (!LensBorderColor.TryParse(value,out var rgb)) return;
            var color=$"#{rgb:X6}";
            if (color == BorderColor) return;
            SavePreferences(_preferences with { BorderColor=color });
            _session.SetBorderColor(rgb);
            Notify(nameof(BorderColor));
        }
    }
    internal uint BorderColorRgb => LensBorderColor.TryParse(BorderColor,out var rgb) ? rgb : LensBorderColor.DefaultRgb;
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
    public string ZoomText => LensZoom.Format(Zoom);
    public string FrameSizeText => $"{EffectiveFrameWidth} × {EffectiveFrameHeight} 像素";
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
    public string MovementHint => IsFixedMode ? "位置固定，鼠标操作作用于下方窗口。" : "拖动画面移动，接近屏幕中心时自动吸附；鼠标操作仅作用于放大框。";
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
        if (!preferences.IsValid) { Status="该按键不可作为快捷键。"; return false; }
        Stop();
        SavePreferences(preferences);
        Notify(nameof(ToggleShortcut)); Notify(nameof(ToggleShortcutText));
        return true;
    }
    private void SavePreferences(GamingPreferences preferences)
    {
        _preferences = preferences;
        if (!_preferencesStore.Save(preferences)) Status="设置未能保存，退出后会丢失。";
    }

    public void Start()
    {
        if (IsActive || !IsEnabled) return;
        RefreshMonitors();
        if (SelectedMonitor is not { } monitor) { Status="未找到可用的显示器。"; return; }
        _session.Start(Monitors,monitor,(int)Math.Round(FrameWidth),(int)Math.Round(FrameHeight),Zoom,Sharpening,FrameRate,FrameSizePreset,AspectRatio);
    }

    public void ResetDefaults()
    {
        Stop();
        _wheel.Reset();
        ImageMode=LensImageMode.Clear;
        Zoom=2; FrameSizePreset=LensFrameSizePreset.Custom; AspectRatio=LensAspectRatio.Screen; FrameWidth=640; FrameHeight=384; FrameRate=0;
        RefreshMonitors();
        MovementMode=LensMovementMode.Fixed; WheelZoomEnabled=true; BorderColor=LensBorderColor.Default;
        _session.ResetPosition(SelectedMonitor);
        _positionResetVersion++;
        Notify(nameof(PositionResetVersion));
        Status="已恢复默认。快捷键、按键模式和收藏已保留。";
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
public sealed record LensSizeOption(LensFrameSizePreset Value,string Label);
public sealed record LensAspectOption(LensAspectRatio Value,string Label);
public sealed record LensImageOption(LensImageMode Value,string Label);
