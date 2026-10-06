using System.Windows;
using System.Windows.Threading;
using DailyToolkit.Core.Gaming;
using Windows.Graphics.Capture;

namespace DailyToolkit.Desktop.Gaming;

// Owns one lens runtime. Frames stay directly between LensCapture and LensGpuRenderer.
internal sealed class ScreenLensSession : IDisposable
{
    private LensNativeWindow? _window;
    private LensGpuRenderer? _renderer;
    private LensCapture? _capture;
    private LensPointerController? _pointer;
    private LensDesktopGuides? _guides;
    private LensLayout? _layout;
    private CaptureMonitor? _activeMonitor;
    private IntPtr _positionMonitor;
    private double _positionX=0.5,_positionY=0.5;
    private int _centerX,_centerY;
    private double _zoom=2,_sharpening=0.35;
    private uint _borderColor=LensBorderColor.DefaultRgb;
    private bool _active;
    private bool _visible;
    private IReadOnlyList<CaptureMonitor> _monitors=[];
    private PixelBounds[] _monitorBounds=[];
    private int _width,_height,_frameRate;
    private (int Left,int Top,int X,int Y)? _pendingMove;
    private bool _moveQueued;
    private int _generation;
    // Changing capture sources must not invalidate an already queued drag.
    private int _movementGeneration;
    private bool _verticalGuide,_horizontalGuide;
    private DispatcherTimer? _startupTimer;

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
    internal bool HasCaptureResources => _window is not null || _renderer is not null || _capture is not null ||
        _pointer is not null || _guides is not null || _startupTimer is not null;

    public void SetMovement(bool movable)
    {
        IsMovableMode=movable;
        if (!movable) { _pointer?.CancelDrag(); ClearGuides(); }
        _window?.SetMovable(movable);
        if (IsVisible) UpdateActiveStatus();
    }
    public void SetWheelZoom(bool enabled)
    {
        WheelZoomEnabled=enabled;
        if (IsVisible) UpdateActiveStatus();
    }
    public void UpdatePicture(double zoom,double sharpening)
    {
        _zoom=zoom; _sharpening=sharpening;
        Reposition();
    }

    public void SetBorderColor(uint color)
    {
        _borderColor=color;
        if (_layout is not null) _capture?.UpdateSource(_layout.Source,_sharpening,color);
    }

    public void Start(IReadOnlyList<CaptureMonitor> monitors,CaptureMonitor monitor,int width,int height,double zoom,double sharpening,int frameRate)
    {
        if (IsActive) return;
        _zoom=zoom; _sharpening=sharpening;
        _monitors=monitors; _monitorBounds=monitors.Select(m => m.Bounds).ToArray();
        _width=width; _height=height; _frameRate=frameRate;
        try
        {
            if (!GraphicsCaptureSession.IsSupported()) { Status = "当前 Windows 图形环境不支持屏幕捕获。"; return; }
            var centerX=monitor.Bounds.Left+(int)Math.Round(monitor.Bounds.Width*(_positionMonitor == monitor.Handle ? _positionX : 0.5));
            var centerY=monitor.Bounds.Top+(int)Math.Round(monitor.Bounds.Height*(_positionMonitor == monitor.Handle ? _positionY : 0.5));
            var layout = LensLayout.Calculate(monitor.Bounds,width,height,_zoom,centerX,centerY);
            _activeMonitor=monitor;
            _centerX=layout.Output.Left+layout.Output.Width/2;
            _centerY=layout.Output.Top+layout.Output.Height/2;
            // Keep the sampled area aligned with the frame when a changed size clamps its position.
            _layout=layout=LensLayout.Calculate(monitor.Bounds,width,height,_zoom,_centerX,_centerY);
            _window = new(layout.Output);
            _window.SetMovable(IsMovableMode);
            _guides = new(monitor.Bounds);
            _renderer = new(_window.Handle,layout.Output.Width,layout.Output.Height);
            _pointer = new(() => IsVisible ? _layout?.Output : null,MoveFrame,delta => ZoomRequested?.Invoke(delta),
                canMove:() => IsMovableMode,dragEnded:ClearGuides,dragStarted:ShowGuides,nativeInput:true,
                canZoom:() => WheelZoomEnabled);
            _window.PointerMessage += _pointer.Process;
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
        var capture=_capture=new(_renderer!,CaptureInterop.ForMonitor(monitor.Handle),layout.Source,_sharpening,_frameRate,_borderColor);
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

    private void MoveFrame(int left,int top,int cursorX,int cursorY)
    {
        if (_layout is null || _activeMonitor is null || !IsMovableMode) return;
        var monitor=_monitors[LensPlacement.MonitorAt(_monitorBounds,cursorX,cursorY)];
        if (monitor.Handle != _activeMonitor.Handle || _moveQueued)
        {
            _pendingMove=(left,top,cursorX,cursorY);
            if (_moveQueued) return;
            _moveQueued=true;
            var generation=_movementGeneration;
            // Switch capture outside the low-level mouse hook; keep only the newest drag position.
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (generation != _movementGeneration) return;
                _moveQueued=false;
                var move=_pendingMove; _pendingMove=null;
                if (!IsActive || !IsMovableMode || move is null) return;
                try { MoveOnMonitor(move.Value.Left,move.Value.Top,
                    _monitors[LensPlacement.MonitorAt(_monitorBounds,move.Value.X,move.Value.Y)]); }
                catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); Stop(); Status="跨屏移动失败，请重新开启放大框。"; }
            });
            return;
        }
        MoveOnMonitor(left,top,monitor);
    }

    private void MoveOnMonitor(int left,int top,CaptureMonitor monitor)
    {
        if (_layout is null || _activeMonitor is null) return;
        var changed=monitor.Handle != _activeMonitor.Handle;
        var placement=LensPlacement.Snap(monitor.Bounds,_width,_height,left,top);
        if (changed)
        {
            _generation++;
            _startupTimer?.Stop();
            _capture?.Dispose(); _capture=null;
            if (_layout.Output.Width != placement.Bounds.Width || _layout.Output.Height != placement.Bounds.Height)
            {
                _renderer?.Dispose();
                _renderer=new(_window!.Handle,placement.Bounds.Width,placement.Bounds.Height);
            }
            var showGuides=_guides?.Visible == true;
            _guides?.Dispose(); _guides=new(monitor.Bounds);
            _activeMonitor=monitor;
            _layout=new(placement.Bounds,_layout.Source);
            if (showGuides) _guides.Update(placement.Bounds,placement.VerticalGuide,placement.HorizontalGuide);
        }
        var bounds=_activeMonitor.Bounds;
        _centerX=placement.Bounds.Left+placement.Bounds.Width/2;
        _centerY=placement.Bounds.Top+placement.Bounds.Height/2;
        _verticalGuide=placement.VerticalGuide; _horizontalGuide=placement.HorizontalGuide;
        _positionMonitor=_activeMonitor.Handle;
        _positionX=(_centerX-bounds.Left)/(double)bounds.Width;
        _positionY=(_centerY-bounds.Top)/(double)bounds.Height;
        Reposition();
        if (changed)
        {
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
            _centerX=monitor.Bounds.Left+monitor.Bounds.Width/2;
            _centerY=monitor.Bounds.Top+monitor.Bounds.Height/2;
        }
        Reposition();
    }

    private void ClearGuides()
    {
        _guides?.Hide();
        _window?.ReleasePointer();
        _verticalGuide=_horizontalGuide=false;
    }

    private void ShowGuides()
    {
        if (_layout is not null) _guides?.Update(_layout.Output,_verticalGuide,_horizontalGuide);
    }

    private void UpdateActiveStatus() => Status=$"已开启 · {_zoom:0.##}× · " +
        (!IsMovableMode ? "固定当前位置" : "按住放大画面拖动") + (WheelZoomEnabled ? "，框内滚轮调倍率" : "，滚轮调节已关闭");

    private void Reposition()
    {
        if (_layout is null || _activeMonitor is null || !IsActive) return;
        var layout=LensLayout.Calculate(_activeMonitor.Bounds,_layout.Output.Width,_layout.Output.Height,_zoom,_centerX,_centerY);
        if (_window?.Move(layout.Output) != true) { Status="浮窗移动失败，请重新开启放大框。"; return; }
        _layout=layout;
        if (_guides is { Visible:true }) _guides.Update(layout.Output,_verticalGuide,_horizontalGuide);
        _capture?.UpdateSource(layout.Source,_sharpening,_borderColor);
        UpdateActiveStatus();
    }

    public void Stop()
    {
        _generation++;
        _movementGeneration++;
        _pointer?.Dispose(); _pointer=null;
        _guides?.Dispose(); _guides=null;
        _startupTimer?.Stop(); _startupTimer=null;
        _window?.Hide();
        _capture?.Dispose(); _capture = null;
        _renderer?.Dispose(); _renderer = null;
        _window?.Dispose(); _window = null;
        _layout=null; _activeMonitor=null;
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
