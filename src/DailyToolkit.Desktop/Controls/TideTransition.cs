using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DailyToolkit.Desktop.Controls;

public sealed class TideTransition : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty=DependencyProperty.Register(nameof(Progress),typeof(double),typeof(TideTransition),
        new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SnapshotProperty=DependencyProperty.Register(nameof(Snapshot),typeof(BitmapSource),typeof(TideTransition),
        new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OriginProperty=DependencyProperty.Register(nameof(Origin),typeof(Point),typeof(TideTransition),
        new FrameworkPropertyMetadata(new Point(),FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty,value); }
    public BitmapSource? Snapshot { get => (BitmapSource?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty,value); }
    public Point Origin { get => (Point)GetValue(OriginProperty); set => SetValue(OriginProperty,value); }
    protected override void OnRender(DrawingContext draw)
    {
        base.OnRender(draw);
        if (Snapshot is null || ActualWidth <= 0 || ActualHeight <= 0 || Progress >= 1) return;
        var bounds=new Rect(0,0,ActualWidth,ActualHeight);
        if (Progress <= 0) { draw.DrawImage(Snapshot,bounds); return; }
        var h=ActualHeight;
        var origin=new Point(Math.Clamp(Origin.X,0,ActualWidth),Math.Clamp(Origin.Y,0,h));
        var x=origin.X+(ActualWidth-origin.X+40)*Progress;
        var reach=(Math.Max(origin.Y,h-origin.Y)+40)*Math.Sqrt(Progress);
        var top=origin.Y-reach; var bottom=origin.Y+reach;
        var left=origin.X-(origin.X+40)*Math.Min(1,Progress*4);
        var bend=26*Math.Sin(Math.PI*Progress);
        var wave=new StreamGeometry();
        using (var path=wave.Open())
        {
            path.BeginFigure(new(left,top),isFilled:true,isClosed:true);
            path.LineTo(new(x,top),true,false);
            path.BezierTo(new(x-bend,top+reach*.33),new(x+bend,top+reach*.67),new(x,origin.Y),true,false);
            path.BezierTo(new(x-bend,origin.Y+reach*.33),new(x+bend,origin.Y+reach*.67),new(x,bottom),true,false);
            path.LineTo(new(left,bottom),true,false);
        }
        wave.Freeze();
        var remaining=new CombinedGeometry(GeometryCombineMode.Exclude,new RectangleGeometry(bounds),wave);
        remaining.Freeze(); draw.PushClip(remaining); draw.DrawImage(Snapshot,bounds); draw.Pop();
    }
}
