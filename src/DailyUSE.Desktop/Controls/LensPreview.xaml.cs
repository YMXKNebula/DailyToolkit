using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DailyUSE.Desktop.Gaming;
using DailyUSE.Desktop.Presentation;

namespace DailyUSE.Desktop.Controls;

public partial class LensPreview : UserControl
{
    private GamingViewModel? _model;
    private LensPhotoPreviewRenderer? _renderer;
    private LensPreviewFrame? _frame;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private bool _dirty=true, _rendering;
    private int _generation;
    private double _pointerX=0.5, _pointerY=0.5;
    private bool _dragging;
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
        _dirty=true;
        UpdateTimer();
    }

    private void SettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(GamingViewModel.FrameWidth) or nameof(GamingViewModel.FrameHeight)
            or nameof(GamingViewModel.Zoom) or nameof(GamingViewModel.Sharpening)
            or nameof(GamingViewModel.SelectedMonitor) or nameof(GamingViewModel.FrameRate))) return;
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
            _pointerX,_pointerY);
        try
        {
            var result=await Task.Run(() => renderer.Render(settings));
            if (generation != _generation) return;
            _frame=result;
            BackgroundPhoto.Source=result.Photo;
            FrameImage.Source=DetailImage.Source=result.Magnified;
            FrameBorder.Visibility=Visibility.Visible;
            ScreenInfo.Text=$"{settings.ScreenWidth} × {settings.ScreenHeight} · 显示器比例";
            FrameInfo.Text=$"{result.Layout.Output.Width} × {result.Layout.Output.Height} 像素 · {settings.Zoom:0.##}× · 锐化 {settings.Sharpening:P0}";
            var clamped=result.Layout.Output.Width != settings.Width || result.Layout.Output.Height != settings.Height;
            PreviewNote.Text=(settings.FrameRate == 0 ? "跟随捕获画面的新帧。" : $"上限 {settings.FrameRate} 帧/秒。") +
                (clamped ? "框已限制在显示器范围内。" : "") + "拖拽上方放大框、滚动鼠标可试效果。照片在本机读取。";
            PositionFrame();
        }
        catch (Exception)
        {
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
    }

    private void MovePreview(object sender, MouseEventArgs e)
    {
        if (!_dragging || _frame is null || Screen.ActualWidth <= 0 || Screen.ActualHeight <= 0) return;
        var position=e.GetPosition(Screen);
        var halfWidth=_frame.Layout.Output.Width/(2d*_frame.Settings.ScreenWidth);
        var halfHeight=_frame.Layout.Output.Height/(2d*_frame.Settings.ScreenHeight);
        _pointerX=Math.Clamp(position.X/Screen.ActualWidth-_dragOffsetX,halfWidth,1-halfWidth);
        _pointerY=Math.Clamp(position.Y/Screen.ActualHeight-_dragOffsetY,halfHeight,1-halfHeight);
        _dirty=true; UpdateTimer();
    }

    private void BeginDrag(object sender,MouseButtonEventArgs e)
    {
        if (_frame is null || Screen.ActualWidth <= 0 || Screen.ActualHeight <= 0) return;
        var position=e.GetPosition(Screen);
        _dragOffsetX=position.X/Screen.ActualWidth-_pointerX;
        _dragOffsetY=position.Y/Screen.ActualHeight-_pointerY;
        _dragging=Screen.CaptureMouse();
        e.Handled=true;
    }

    private void EndDrag(object sender,MouseButtonEventArgs e) { _dragging=false; Screen.ReleaseMouseCapture(); }
    private void LostDrag(object sender,MouseEventArgs e) => _dragging=false;
    private void ZoomPreview(object sender,MouseWheelEventArgs e) { _model?.AdjustZoom(e.Delta); e.Handled=true; }

    private void Release()
    {
        _generation++; _timer.Stop();
        _dragging=false; Screen.ReleaseMouseCapture();
        if (_model is not null) _model.PropertyChanged -= SettingsChanged;
        _model=null;
        var renderer=_renderer; _renderer=null;
        if (renderer is not null) _=Task.Run(renderer.Dispose);
    }
}
