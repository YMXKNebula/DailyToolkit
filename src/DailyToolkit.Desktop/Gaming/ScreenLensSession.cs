using System.Windows;
using System.Windows.Threading;
using DailyToolkit.Core.Gaming;
using Windows.Graphics.Capture;

namespace DailyToolkit.Desktop.Gaming;

// Owns one lens runtime. Frames stay directly between LensCapture and LensGpuRenderer.
internal sealed class ScreenLensSession(bool nativePointer=true) : IDisposable
{
    private LensNativeWindow? _window;
    private LensGpuRenderer? _renderer;
    private LensCapture? _capture;
    private LensPointerController? _pointer;
    private LensDesktopGuides? _guides;
    private LensLayout? _layout;
    private LensMovement? _movement;
    private LensMovementState? _appliedMovement;
    private CaptureMonitor? _activeMonitor;
    private IntPtr _positionMonitor;
    private double _positionX=0.5,_positionY=0.5;
    private double _zoom=2,_sharpening=0.35;
    private LensImageMode _imageMode=LensImageMode.Clear;
    private uint _borderColor=LensBorderColor.DefaultRgb;
    private bool _active;
    private bool _visible;
    private IReadOnlyList<CaptureMonitor> _monitors=[];
    private PixelBounds[] _monitorBounds=[];
    private int _width,_height,_frameRate;
    private LensFrameSizePreset _sizePreset;
    private LensAspectRatio _aspect;
    private LensMovementState? _pendingMove;
    private bool _moveQueued;
    private int _generation;
    // Changing capture sources must not invalidate an already queued drag.
    private int _movementGeneration;
    private bool _verticalGuide,_horizontalGuide;
    private DispatcherTimer? _startupTimer;
    private DispatcherTimer? _hudTimer;
    private readonly LensZoomHud _zoomHud = new();
    private readonly List<int> _pendingWheelRuns = new(8);
    private bool _wheelQueued;
    private int _wheelGeneration;
    private static double HudTime => System.Diagnostics.Stopwatch.GetTimestamp()/(double)System.Diagnostics.Stopwatch.Frequency;

    public event Action? StateChanged;
    public event Action? VisibilityChanged;
    public event Action<string>? StatusChanged;
    public event Action<int>? ZoomRequested;
    public event Action<CaptureMonitor>? MonitorChanged;
    private string Status { set => StatusChanged?.Invoke(value); }
    public bool IsActive => _active;
    public bool IsVisible => IsActive && _visible;
    private bool IsRequestedVisible => IsActive;
    public bool IsMovableMode { get; private set; }
    public bool WheelZoomEnabled { get; private set; } = true;
    internal PixelBounds? ActiveBounds => _layout?.Output;
    internal LensPointerController? PointerForDiagnostics => _pointer;
    internal bool VerticalGuide => _verticalGuide;
    internal bool HorizontalGuide => _horizontalGuide;
    internal IntPtr LensHandle => _window?.Handle ?? IntPtr.Zero;
    internal LensDesktopGuides? GuidesForDiagnostics => _guides;
    internal LensCapture? CaptureForDiagnostics => _capture;
    internal LensGpuRenderer? RendererForDiagnostics => _renderer;
    internal LensMovementState? MovementForDiagnostics => _appliedMovement;
    internal bool HasCaptureResources => _window is not null || _renderer is not null || _capture is not null ||
        _pointer is not null || _guides is not null || _startupTimer is not null;

    public void SetMovement(bool movable)
    {
        IsMovableMode=movable;
        if (!movable)
        {
            _movementGeneration++;
            _pendingMove=null; _moveQueued=false;
            if (_appliedMovement is not null) _movement?.Restore(_appliedMovement);
            _pointer?.CancelDrag(); ClearGuides();
        }
        _movement?.SetMovable(movable);
        _window?.SetMovable(movable);
        if (IsVisible) UpdateActiveStatus();
    }
    public void SetWheelZoom(bool enabled)
    {
        WheelZoomEnabled=enabled;
        if (!enabled) { _wheelGeneration++; _pendingWheelRuns.Clear(); _wheelQueued=false; }
        if (IsVisible) UpdateActiveStatus();
    }
    public void UpdatePicture(double zoom,double sharpening)
    {
        _zoom=zoom; _sharpening=sharpening;
        if (_movement is null) return;
        _movement.UpdateZoom(zoom);
        if (_moveQueued) _pendingMove=_movement.State;
        else ApplyMovement(_movement.State);
        if (IsVisible) UpdateActiveStatus();
    }

    // The pointer callback only accumulates deltas and posts one dispatcher operation.
    // Persistence, geometry updates, HUD and rendering happen after the callback returns.
    private void QueueWheelZoom(int delta)
    {
        if (delta == 0) return;
        // Merge equal directions but retain reversals: +120 then -120 at 16x must end at 15.75x.
        if (_pendingWheelRuns.Count > 0 && Math.Sign(_pendingWheelRuns[^1]) == Math.Sign(delta))
            _pendingWheelRuns[^1]=(int)Math.Clamp((long)_pendingWheelRuns[^1]+delta,int.MinValue,int.MaxValue);
        else _pendingWheelRuns.Add(delta);
        if (_wheelQueued) return;
        _wheelQueued=true;
        var generation=_wheelGeneration;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(() =>
        {
            if (generation != _wheelGeneration) return;
            var pending=_pendingWheelRuns.ToArray();
            _pendingWheelRuns.Clear(); _wheelQueued=false;
            foreach (var run in pending)
                if (generation == _wheelGeneration && IsActive && WheelZoomEnabled) ZoomRequested?.Invoke(run);
        }));
    }

    public void ShowZoomHud(double zoom)
    {
        if (!IsVisible) return;
        _zoomHud.Show(zoom,HudTime);
        _capture?.UpdateHud(_zoomHud.Frame(HudTime));
        if (_hudTimer is null)
        {
            _hudTimer=new(DispatcherPriority.Background) { Interval=TimeSpan.FromMilliseconds(16) };
            _hudTimer.Tick += (_,_) =>
            {
                var hud=_zoomHud.Frame(HudTime);
                _capture?.UpdateHud(hud);
                if (hud.Opacity <= 0) _hudTimer.Stop();
            };
        }
        _hudTimer.Start();
    }

    public void SetBorderColor(uint color)
    {
        _borderColor=color;
        if (_layout is not null) _capture?.UpdateSource(_layout.Source,_sharpening,color);
    }

    public void SetImageMode(LensImageMode mode)
    {
        _imageMode=LensImageSettings.NormalizeMode(mode);
        _capture?.UpdateImageMode(_imageMode);
    }

    public void Start(IReadOnlyList<CaptureMonitor> monitors,CaptureMonitor monitor,int width,int height,double zoom,double sharpening,int frameRate,
        LensFrameSizePreset sizePreset=LensFrameSizePreset.Custom,LensAspectRatio aspect=LensAspectRatio.Screen)
    {
        if (IsActive) return;
        _zoom=zoom; _sharpening=sharpening;
        _monitors=monitors; _monitorBounds=monitors.Select(m => m.Bounds).ToArray();
        _width=width; _height=height; _frameRate=frameRate; _sizePreset=sizePreset; _aspect=aspect;
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) { Status = "当前 Windows 图形环境不支持屏幕捕获。"; return; }
            var centerX=monitor.Bounds.Left+(int)Math.Round(monitor.Bounds.Width*(_positionMonitor == monitor.Handle ? _positionX : 0.5));
            var centerY=monitor.Bounds.Top+(int)Math.Round(monitor.Bounds.Height*(_positionMonitor == monitor.Handle ? _positionY : 0.5));
            var index=Array.FindIndex(_monitorBounds,b => b == monitor.Bounds);
            _movement=new(_monitorBounds,index,width,height,_zoom,centerX,centerY,IsMovableMode,centerSnap:true,sizePreset:_sizePreset,aspect:_aspect);
            var layout=_movement.State.Layout;
            _activeMonitor=monitor;
            _appliedMovement=_movement.State;
            _layout=layout;
            _window = new(layout.Output);
            _window.SetMovable(IsMovableMode);
            _guides = new(monitor.Bounds);
            _renderer = new(_window.Handle,layout.Output.Width,layout.Output.Height);
            _pointer = new(() => IsVisible ? _layout?.Output : null,MoveFrame,QueueWheelZoom,
                install:nativePointer,
                canMove:() => IsMovableMode,dragEnded:ClearGuides,dragStarted:ShowGuides,nativeInput:true,
                canZoom:() => WheelZoomEnabled);
            if (nativePointer) _window.PointerMessage += _pointer.Process;
            _window.PointerCanceled += _pointer.CancelDrag;
            _active = true;
            StateChanged?.Invoke();
            Status = "正在读取屏幕画面…";
            BeginCapture(monitor,layout);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine(exception);
            Stop();
            Status = "没有开启成功。请使用窗口或无边框模式，并确认 Windows 允许屏幕捕获。";
        }
    }

    private void BeginCapture(CaptureMonitor monitor,LensLayout layout)
    {
        var generation=++_generation;
        var capture=_capture=new(_renderer!,CaptureInterop.ForMonitor(monitor.Handle),layout.Source,_sharpening,_frameRate,_borderColor,_imageMode);
        capture.UpdateHud(_zoomHud.Frame(HudTime));
        capture.FirstFrame += () => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (generation != _generation || !IsActive) return;
            _startupTimer?.Stop();
            _visible=true;
            _window?.Show(); UpdateActiveStatus();
            VisibilityChanged?.Invoke();
        });
        capture.Failed += reason => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (generation != _generation) return;
            Stop(); Status = reason;
        });
        _startupTimer?.Stop();
        _startupTimer = new DispatcherTimer { Interval=TimeSpan.FromSeconds(5) };
        _startupTimer.Tick += (_,_) =>
        {
            if (generation != _generation || !IsRequestedVisible) return;
            Stop(); Status="暂时没有取得画面，请确认 Windows 允许捕获，并使用窗口或无边框模式。";
        };
        _=StartCaptureAsync(capture,generation);
    }

    private async Task StartCaptureAsync(LensCapture capture,int generation)
    {
        try
        {
            await capture.StartAsync();
            if (generation == _generation && IsActive) _startupTimer?.Start();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine(exception);
            if (generation != _generation) return;
            Stop();
            Status = "没有开启成功。请使用窗口或无边框模式，并确认 Windows 允许屏幕捕获。";
        }
    }

    private void MoveFrame(int dx,int dy,int cursorX,int cursorY)
    {
        if (_movement is null || _activeMonitor is null || !IsMovableMode) return;
        // Consume every delta immediately, but keep native window/GDI work out of
        // the low-level hook: Windows has not delivered the cursor movement yet.
        _pendingMove=_movement.Move(dx,dy,cursorX,cursorY);
        if (_moveQueued) return;
        _moveQueued=true;
        var generation=_movementGeneration;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Input,new Action(() =>
        {
            if (generation != _movementGeneration) return;
            _moveQueued=false;
            var move=_pendingMove; _pendingMove=null;
            if (!IsActive || !IsMovableMode || move is null) return;
            try { ApplyMovement(move); }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); Stop(); Status="放大框移动失败，请重新开启。"; }
        }));
    }

    private void ApplyMovement(LensMovementState state)
    {
        if (_layout is null || _activeMonitor is null) return;
        var monitor=_monitors[state.MonitorIndex];
        var changed=monitor.Handle != _activeMonitor.Handle;
        var layout=state.Layout;
        if (_window?.Move(layout.Output) != true)
        {
            if (_appliedMovement is not null) _movement?.Restore(_appliedMovement);
            Status="浮窗移动失败，请重新开启放大框。"; return;
        }
        if (changed)
        {
            _generation++;
            _startupTimer?.Stop();
            _capture?.Dispose(); _capture=null;
            if (_layout.Output.Width != layout.Output.Width || _layout.Output.Height != layout.Output.Height)
            {
                _renderer?.Dispose();
                _renderer=new(_window!.Handle,layout.Output.Width,layout.Output.Height);
            }
            var showGuides=_guides?.Visible == true;
            _guides?.Dispose(); _guides=new(monitor.Bounds);
            _activeMonitor=monitor;
            if (showGuides) _guides.Update(layout.Output,state.VerticalGuide,state.HorizontalGuide);
        }
        var bounds=_activeMonitor.Bounds;
        _layout=layout; _appliedMovement=state;
        _verticalGuide=state.VerticalGuide; _horizontalGuide=state.HorizontalGuide;
        _positionMonitor=_activeMonitor.Handle;
        _positionX=(state.X.OutputCenter-bounds.Left)/bounds.Width;
        _positionY=(state.Y.OutputCenter-bounds.Top)/bounds.Height;
        if (_guides is { Visible:true }) _guides.Update(layout.Output,_verticalGuide,_horizontalGuide);
        if (!changed) _capture?.UpdateSource(layout.Source,_sharpening,_borderColor);
        if (changed)
        {
            UpdateActiveStatus();
            BeginCapture(monitor,_layout!);
            MonitorChanged?.Invoke(monitor);
        }
    }

    public void ResetPosition(CaptureMonitor? selectedMonitor)
    {
        _pointer?.CancelDrag();
        _verticalGuide=_horizontalGuide=false;
        var monitor=_activeMonitor ?? selectedMonitor;
        _positionMonitor=monitor?.Handle ?? IntPtr.Zero;
        _positionX=_positionY=0.5;
        if (monitor is not null)
        {
            if (_movement is not null)
            {
                _movementGeneration++; _pendingMove=null; _moveQueued=false;
                var index=Array.FindIndex(_monitorBounds,b => b == monitor.Bounds);
                _movement=new(_monitorBounds,index,_width,_height,_zoom,
                    monitor.Bounds.Left+monitor.Bounds.Width/2d,monitor.Bounds.Top+monitor.Bounds.Height/2d,
                    IsMovableMode,centerSnap:true,sizePreset:_sizePreset,aspect:_aspect);
                ApplyMovement(_movement.State);
            }
        }
    }

    private void ClearGuides()
    {
        _guides?.Hide();
        _window?.ReleasePointer();
        _verticalGuide=_horizontalGuide=false;
    }

    private void ShowGuides()
    {
        _movement?.BeginDrag();
        if (_layout is not null) _guides?.Update(_layout.Output,_verticalGuide,_horizontalGuide);
    }

    private void UpdateActiveStatus() => Status=$"已开启 · {LensZoom.Format(_zoom)} · " +
        (!IsMovableMode ? "固定当前位置" : "按住放大画面拖动") + (WheelZoomEnabled ? "，框内滚轮调倍率" : "，滚轮调节已关闭");

    public void Stop()
    {
        _generation++;
        _movementGeneration++;
        _wheelGeneration++; _pendingWheelRuns.Clear(); _wheelQueued=false;
        _hudTimer?.Stop(); _zoomHud.Clear();
        _pointer?.Dispose(); _pointer=null;
        _guides?.Dispose(); _guides=null;
        _startupTimer?.Stop(); _startupTimer=null;
        _window?.Hide();
        _capture?.Dispose(); _capture = null;
        _renderer?.Dispose(); _renderer = null;
        _window?.Dispose(); _window = null;
        _layout=null; _activeMonitor=null; _movement=null; _appliedMovement=null;
        _visible=false;
        _pendingMove=null;
        _moveQueued=false;
        _verticalGuide=_horizontalGuide=false;
        _active = false;
        Status = "未开启";
        StateChanged?.Invoke();
    }
    public void Dispose() => Stop();
}
