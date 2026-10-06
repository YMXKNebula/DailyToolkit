using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Controls;

public partial class LensPreview : UserControl
{
    private GamingViewModel? _model;
    private LensPhotoPreviewRenderer? _renderer;
    private LensPreviewFrame? _frame;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private bool _dirty=true, _rendering;
    private int _generation;
    private double _pointerX=0.5,_pointerY=0.5;
    private bool _dragging;
    private bool _verticalGuide,_horizontalGuide;
    private double _dragOffsetX,_dragOffsetY;
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task Ready => _ready.Task;
    internal LensPreviewFrame? CurrentFrame => _frame;

    public LensPreview()
    {
        InitializeComponent();
        _timer.Tick += (_,_) => RenderPending();
        Loaded += (_,_) => Attach();
        DataContextChanged += (_,_) => Attach();
        IsVisibleChanged += (_,_) => UpdateTimer();
        Unloaded += (_,_) => Release();
    }

    private void Attach()
    {
        if (!IsLoaded) return;
        if (_model is not null) _model.PropertyChanged -= SettingsChanged;
        _model=DataContext as GamingViewModel;
        if (_model is not null) _model.PropertyChanged += SettingsChanged;
        UpdateMovement();
        _dirty=true;
        UpdateTimer();
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GamingViewModel.PositionResetVersion))
        {
            FinishDrag(); _pointerX=_pointerY=0.5;
        }
        if (e.PropertyName is nameof(GamingViewModel.MovementMode) or nameof(GamingViewModel.WheelZoomEnabled)) UpdateMovement();
        if (e.PropertyName is not (nameof(GamingViewModel.FrameWidth) or nameof(GamingViewModel.FrameHeight)
            or nameof(GamingViewModel.Zoom) or nameof(GamingViewModel.Sharpening)
            or nameof(GamingViewModel.SelectedMonitor) or nameof(GamingViewModel.FrameRate)
            or nameof(GamingViewModel.MovementMode) or nameof(GamingViewModel.PositionResetVersion)
            or nameof(GamingViewModel.WheelZoomEnabled))) return;
        _dirty=true;
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        if (!IsLoaded || !IsVisible || _model is null) { _timer.Stop(); return; }
        _timer.Interval=TimeSpan.FromSeconds(1d/(_model.FrameRate == 0 ? 60 : Math.Min(60,_model.FrameRate)));
        // Wake only for changes. A static photo does not need continuous rendering.
        if (_dirty) _timer.Start();
    }

    private async void RenderPending()
    {
        if (!_dirty || _rendering || _model is null) return;
        if (_model.SelectedMonitor is not { } monitor)
        {
            _timer.Stop(); _dirty=false;
            PreviewNote.Text="没有可用的显示器，暂时无法预览。";
            _ready.TrySetResult();
            return;
        }
        _dirty=false; _rendering=true;
        _timer.Stop();
        var generation=_generation;
        var renderer=_renderer ??= new();
        var settings = new LensPreviewSettings(monitor.Bounds.Width,monitor.Bounds.Height,
            (int)_model.FrameWidth,(int)_model.FrameHeight,_model.Zoom,_model.Sharpening,_model.FrameRate,
            _pointerX,_pointerY,_verticalGuide,_horizontalGuide);
        try
        {
            var result=await Task.Run(() => renderer.Render(settings));
            if (generation != _generation) return;
            _frame=result;
            BackgroundPhoto.Source=result.Photo;
            FrameImage.Source=DetailImage.Source=result.Magnified;
            FrameBorder.Visibility=Visibility.Visible;
            ScreenInfo.Text=$"{settings.ScreenWidth} × {settings.ScreenHeight} · 显示器比例";
            FrameInfo.Text=$"{result.Layout.Output.Width} × {result.Layout.Output.Height} 像素 · {settings.Zoom:0.##}×";
            var clamped=result.Layout.Output.Width != settings.Width || result.Layout.Output.Height != settings.Height;
            PreviewNote.Text=(settings.FrameRate == 0 ? "跟随捕获画面的新帧。" : $"上限 {settings.FrameRate} 帧/秒。") +
                (clamped ? "框已限制在显示器范围内。" : "") +
                (_model.IsFixedMode ? "锁住当前位置。" : "拖拽上方放大框，接近中心会吸附。") +
                (_model.WheelZoomEnabled ? "滚轮可试倍率。" : "滚轮调节已关闭。") +
                "预览位置独立，照片在本机读取。";
            PositionFrame();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine(exception);
            if (generation == _generation) PreviewNote.Text="暂时无法生成预览，可以继续调整设置和使用放大框。";
        }
        finally
        {
            _rendering=false;
            if (generation == _generation) _ready.TrySetResult();
            UpdateTimer();
        }
    }

    private void ScreenSizeChanged(object sender, SizeChangedEventArgs e) => PositionFrame();

    private void PositionFrame()
    {
        if (_frame is null || Screen.ActualWidth <= 0) return;
        var settings=_frame.Settings;
        Screen.Height=Screen.ActualWidth*settings.ScreenHeight/settings.ScreenWidth;
        var scale=Screen.ActualWidth/settings.ScreenWidth;
        var output=_frame.Layout.Output;
        Canvas.SetLeft(FrameBorder,output.Left*scale);
        Canvas.SetTop(FrameBorder,output.Top*scale);
        FrameBorder.Width=output.Width*scale;
        FrameBorder.Height=output.Height*scale;
        VerticalGuide.X1=VerticalGuide.X2=settings.ScreenWidth/2d*scale;
        VerticalGuide.Y1=0; VerticalGuide.Y2=Screen.Height;
        HorizontalGuide.Y1=HorizontalGuide.Y2=settings.ScreenHeight/2d*scale;
        HorizontalGuide.X1=0; HorizontalGuide.X2=Screen.ActualWidth;
        VerticalGuide.Visibility=_dragging && _verticalGuide ? Visibility.Visible : Visibility.Collapsed;
        HorizontalGuide.Visibility=_dragging && _horizontalGuide ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MovePreview(object sender, MouseEventArgs e)
    {
        if (!_dragging || _model?.IsMovableMode != true || _frame is null || Screen.ActualWidth <= 0 || Screen.ActualHeight <= 0) return;
        var position=e.GetPosition(Screen);
        var settings=_frame.Settings;
        var width=_frame.Layout.Output.Width; var height=_frame.Layout.Output.Height;
        var left=(int)Math.Round((position.X/Screen.ActualWidth-_dragOffsetX)*settings.ScreenWidth)-width/2;
        var top=(int)Math.Round((position.Y/Screen.ActualHeight-_dragOffsetY)*settings.ScreenHeight)-height/2;
        var placement=LensPlacement.Snap(new(0,0,settings.ScreenWidth,settings.ScreenHeight),width,height,left,top);
        _pointerX=(placement.Bounds.Left+width/2)/(double)settings.ScreenWidth;
        _pointerY=(placement.Bounds.Top+height/2)/(double)settings.ScreenHeight;
        _verticalGuide=placement.VerticalGuide; _horizontalGuide=placement.HorizontalGuide;
        _dirty=true; UpdateTimer();
    }

    private void BeginDrag(object sender,MouseButtonEventArgs e)
    {
        if (_model?.IsMovableMode != true || _frame is null || Screen.ActualWidth <= 0 || Screen.ActualHeight <= 0) return;
        var position=e.GetPosition(Screen);
        var settings=_frame.Settings;
        var output=_frame.Layout.Output;
        _dragOffsetX=position.X/Screen.ActualWidth-(output.Left+output.Width/2)/(double)settings.ScreenWidth;
        _dragOffsetY=position.Y/Screen.ActualHeight-(output.Top+output.Height/2)/(double)settings.ScreenHeight;
        _dragging=Screen.CaptureMouse();
        e.Handled=true;
    }

    private void UpdateMovement()
    {
        FrameBorder.Cursor=_model?.IsFixedMode == true ? Cursors.Arrow : Cursors.SizeAll;
        Screen.ToolTip=(_model?.IsFixedMode == true ? "锁住当前位置。" : "拖拽照片中的放大框，靠近中心时吸附。") +
            (_model?.WheelZoomEnabled == true ? "滚动鼠标调整倍率。" : "滚轮调节已关闭。");
        if (_model?.IsFixedMode == true) FinishDrag();
    }

    private void FinishDrag()
    {
        _dragging=false;
        _verticalGuide=_horizontalGuide=false;
        VerticalGuide.Visibility=HorizontalGuide.Visibility=Visibility.Collapsed;
        if (Screen.IsMouseCaptured) Screen.ReleaseMouseCapture();
        _dirty=true; UpdateTimer();
    }
    private void EndDrag(object sender,MouseButtonEventArgs e) => FinishDrag();
    private void LostDrag(object sender,MouseEventArgs e) => FinishDrag();
    private void ZoomPreview(object sender,MouseWheelEventArgs e)
    {
        if (_model?.WheelZoomEnabled != true) return;
        _model.AdjustZoom(e.Delta); e.Handled=true;
    }

    private void Release()
    {
        FinishDrag();
        _generation++; _timer.Stop();
        _dragging=false; Screen.ReleaseMouseCapture();
        if (_model is not null) _model.PropertyChanged -= SettingsChanged;
        _model=null;
        var renderer=_renderer; _renderer=null;
        if (renderer is not null) _=Task.Run(renderer.Dispose);
    }
}
