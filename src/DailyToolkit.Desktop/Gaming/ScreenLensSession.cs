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
    private bool _active;
    private int _generation;
    private bool _verticalGuide,_horizontalGuide;
    private DispatcherTimer? _startupTimer;

    public event Action? StateChanged;
    public event Action? VisibilityChanged;
    public event Action<string>? StatusChanged;
    public event Action<int>? ZoomRequested;
    private string Status { set => StatusChanged?.Invoke(value); }
    public bool IsActive => _active;
    public bool IsVisible => IsActive && _capture is { FramesRendered: > 0 };
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

    public void Start(CaptureMonitor monitor,int width,int height,double zoom,double sharpening,int frameRate)
    {
        if (IsActive) return;
        _zoom=zoom; _sharpening=sharpening;
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
            var item = CaptureInterop.ForMonitor(monitor.Handle);
            var generation = ++_generation;
            _capture = new(_renderer,item,layout.Source,_sharpening,frameRate);
            _pointer = new(() => IsVisible ? _layout?.Output : null,MoveFrame,delta => ZoomRequested?.Invoke(delta),
                canMove:() => IsMovableMode,dragEnded:ClearGuides,dragStarted:ShowGuides,nativeInput:true,
                canZoom:() => WheelZoomEnabled);
            _window.PointerMessage += _pointer.Process;
            _window.PointerCanceled += _pointer.CancelDrag;
            _capture.FirstFrame += () => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (generation != _generation || !IsActive) return;
                _startupTimer?.Stop();
                _window?.Show(); UpdateActiveStatus();
                VisibilityChanged?.Invoke();
            });
            _capture.Failed += reason => Application.Current.Dispatcher.BeginInvoke(() =>
            {
                if (generation != _generation) return;
                Stop(); Status = reason;
            });
            _active = true;
            StateChanged?.Invoke();
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
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine(exception);
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
        _capture?.UpdateSource(layout.Source,_sharpening);
        UpdateActiveStatus();
    }

    public void Stop()
    {
        _generation++;
        _pointer?.Dispose(); _pointer=null;
        _guides?.Dispose(); _guides=null;
        _startupTimer?.Stop(); _startupTimer=null;
        _window?.Hide();
        _capture?.Dispose(); _capture = null;
        _renderer?.Dispose(); _renderer = null;
        _window?.Dispose(); _window = null;
        _layout=null; _activeMonitor=null;
        _verticalGuide=_horizontalGuide=false;
        _active = false;
        Status = "未开启";
        StateChanged?.Invoke();
    }
    public void Dispose() => Stop();
}
