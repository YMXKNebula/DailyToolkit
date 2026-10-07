using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DailyToolkit.Desktop.Controls;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop;

public partial class MainWindow
{
    private Storyboard? _navigationStoryboard;
    private int _navigationGeneration;

    private BitmapSource CaptureBodySnapshot()
    {
        RootContent.UpdateLayout();
        var dpi=VisualTreeHelper.GetDpi(RootContent);
        var frame=new RenderTargetBitmap((int)Math.Ceiling(RootContent.ActualWidth*dpi.DpiScaleX),
            (int)Math.Ceiling(RootContent.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        frame.Render(RootContent); frame.Freeze();
        var origin=BodyContent.TranslatePoint(new Point(),RootContent);
        var crop=new CroppedBitmap(frame,new Int32Rect((int)Math.Round(origin.X*dpi.DpiScaleX),(int)Math.Round(origin.Y*dpi.DpiScaleY),
            (int)Math.Ceiling(BodyContent.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(BodyContent.ActualHeight*dpi.DpiScaleY)));
        crop.Freeze(); return crop;
    }

    private void NavigateWithTide(object sender,RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not NavigationItem item || item.Id == _viewModel.Page) return;
        var animate=_viewModel.PageAnimationsEnabled && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast && IsVisible;
        var snapshot=animate ? CaptureBodySnapshot() : null;
        FinishPageTransition(); FinishNavigationTransition();
        if (snapshot is null) { _viewModel.NavigateCommand.Execute(item); return; }
        NavigationTransition.Snapshot=snapshot; NavigationTransition.Progress=0; NavigationTransition.Visibility=Visibility.Visible;
        MainContent.IsHitTestVisible=false;
        _viewModel.NavigateCommand.Execute(item);
        var generation=_navigationGeneration;
        var storyboard=_navigationStoryboard=new Storyboard();
        var animation=new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(220)) { EasingFunction=new SineEase { EasingMode=EasingMode.EaseInOut } };
        Storyboard.SetTarget(animation,NavigationTransition); Storyboard.SetTargetProperty(animation,new PropertyPath(TideTransition.ProgressProperty));
        storyboard.Children.Add(animation);
        storyboard.Completed += (_,_) => { if (generation == _navigationGeneration) FinishNavigationTransition(); };
        storyboard.Begin(this,isControllable:true);
    }

    internal void SeekNavigationTransition(TimeSpan offset) => _navigationStoryboard?.SeekAlignedToLastTick(this,offset,TimeSeekOrigin.BeginTime);
    private void FinishNavigationTransition()
    {
        ++_navigationGeneration;
        _navigationStoryboard?.Remove(this); _navigationStoryboard=null;
        NavigationTransition.Snapshot=null; NavigationTransition.Visibility=Visibility.Collapsed;
        MainContent.IsHitTestVisible=true;
    }

    private void NavigationRowSizeChanged(object sender,SizeChangedEventArgs e) => UpdateNavigationBridge();
    private void UpdateNavigationBridge()
    {
        if (_viewModel is null) return;
        Border? FindRow(DependencyObject parent)
        {
            if (parent is Border { Name:"NavigationRow",DataContext:NavigationItem { IsSelected:true } } row) return row;
            for (var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
                if (FindRow(VisualTreeHelper.GetChild(parent,i)) is { } found) return found;
            return null;
        }
        if (FindRow(NavigationList) is not { IsVisible:true } selected) { NavigationBridge.Visibility=Visibility.Collapsed; return; }
        var top=selected.TranslatePoint(new Point(),SidebarSurface).Y;
        NavigationBridge.Margin=new Thickness(0,top-8,0,0); NavigationBridge.Height=selected.ActualHeight+16;
        NavigationBridge.Visibility=Visibility.Visible;
    }

    private void ThemeEditorSizeChanged(object sender,SizeChangedEventArgs e)
    {
        var wide=e.NewSize.Width >= 620;
        ThemeEditorGrid.ColumnDefinitions[0].Width=new GridLength(1,GridUnitType.Star);
        ThemeEditorGrid.ColumnDefinitions[1].Width=wide ? new GridLength(1.15,GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(ThemeDetails,wide ? 1 : 0); Grid.SetRow(ThemeDetails,wide ? 0 : 1);
        ThemePreviewPane.Margin=wide ? new Thickness(0,0,20,0) : new Thickness(0,0,0,18);
    }

    private void UpdateWindowCorners()
    {
        var radius=WindowState == WindowState.Maximized ? 0 : 12;
        System.Windows.Shell.WindowChrome.GetWindowChrome(this).CornerRadius=new CornerRadius(radius);
    }
    private void SetNativeRoundedCorners()
    {
        var preference=2; // DWMWCP_ROUND; older Windows versions use WindowChrome's region.
        _=DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,33,ref preference,sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
}
