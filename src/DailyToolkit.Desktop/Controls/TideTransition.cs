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
    public static readonly DependencyProperty ConnectionXProperty=DependencyProperty.Register(nameof(ConnectionX),typeof(double),typeof(TideTransition),
        new FrameworkPropertyMetadata(188d,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty OptionHeightProperty=DependencyProperty.Register(nameof(OptionHeight),typeof(double),typeof(TideTransition),
        new FrameworkPropertyMetadata(40d,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty WaveBrushProperty=DependencyProperty.Register(nameof(WaveBrush),typeof(Brush),typeof(TideTransition),
        new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty,value); }
    public BitmapSource? Snapshot { get => (BitmapSource?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty,value); }
    public Point Origin { get => (Point)GetValue(OriginProperty); set => SetValue(OriginProperty,value); }
    public double ConnectionX { get => (double)GetValue(ConnectionXProperty); set => SetValue(ConnectionXProperty,value); }
    public double OptionHeight { get => (double)GetValue(OptionHeightProperty); set => SetValue(OptionHeightProperty,value); }
    public Brush? WaveBrush { get => (Brush?)GetValue(WaveBrushProperty); set => SetValue(WaveBrushProperty,value); }
    protected override void OnRender(DrawingContext draw)
    {
        base.OnRender(draw);
        if (Snapshot is null || ActualWidth <= 0 || ActualHeight <= 0 || Progress >= 1) return;
        var bounds=new Rect(0,0,ActualWidth,ActualHeight);
        if (Progress <= 0) { draw.DrawImage(Snapshot,bounds); return; }
        var h=ActualHeight;
        var origin=new Point(Math.Clamp(Origin.X,0,ActualWidth),Math.Clamp(Origin.Y,0,h));
        // First travel along the selected row, then spread into its connected panel.
        var connection=Math.Clamp(ConnectionX,origin.X,ActualWidth);
        var rowProgress=Math.Min(1,Progress/.3);
        var panelProgress=Math.Clamp((Progress-.3)/.7,0,1);
        var travel=Math.Sin(panelProgress*Math.PI/2);
        var x=panelProgress == 0 ? origin.X+(connection-origin.X)*Math.Sin(rowProgress*Math.PI/2) :
            connection+(ActualWidth-connection+40)*travel;
        var rowReach=Math.Max(1,OptionHeight/2);
        var reach=rowReach+(Math.Max(origin.Y,h-origin.Y)+40-rowReach)*travel;
        var top=origin.Y-reach; var bottom=origin.Y+reach;
        var left=origin.X-(origin.X+40)*Math.Min(1,panelProgress*4);
        var bend=Math.Min(20,reach*.35)*Math.Sin(Math.PI*Progress);
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
        if (WaveBrush is SolidColorBrush color)
        {
            var transparent=color.Color with { A=0 }; var tint=color.Color with { A=42 };
            var edge=new LinearGradientBrush(new GradientStopCollection { new(transparent,0),new(tint,.55),new(transparent,1) },0);
            edge.Freeze(); draw.PushClip(new RectangleGeometry(bounds)); draw.PushClip(wave);
            draw.DrawRectangle(edge,null,new Rect(x-24,top,48,bottom-top)); draw.Pop(); draw.Pop();
        }
    }
}
