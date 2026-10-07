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
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty,value); }
    public BitmapSource? Snapshot { get => (BitmapSource?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty,value); }
    protected override void OnRender(DrawingContext draw)
    {
        base.OnRender(draw);
        if (Snapshot is null || ActualWidth <= 0 || ActualHeight <= 0 || Progress >= 1) return;
        var bounds=new Rect(0,0,ActualWidth,ActualHeight);
        if (Progress <= 0) { draw.DrawImage(Snapshot,bounds); return; }
        var x=-40+(ActualWidth+80)*Progress;
        var bend=26*Math.Sin(Math.PI*Progress); var h=ActualHeight;
        var wave=new StreamGeometry();
        using (var path=wave.Open())
        {
            path.BeginFigure(new(x,0),isFilled:true,isClosed:true);
            path.LineTo(new(ActualWidth+40,0),true,false); path.LineTo(new(ActualWidth+40,h),true,false);
            path.LineTo(new(x,h),true,false);
            path.BezierTo(new(x-bend,h*.83),new(x+bend,h*.67),new(x,h*.5),true,false);
            path.BezierTo(new(x-bend,h*.33),new(x+bend,h*.17),new(x,0),true,false);
        }
        wave.Freeze(); draw.PushClip(wave); draw.DrawImage(Snapshot,bounds); draw.Pop();
    }
}
