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
    public static readonly DependencyProperty WaveBrushProperty=DependencyProperty.Register(nameof(WaveBrush),typeof(Brush),typeof(TideTransition),
        new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RegionProperty=DependencyProperty.Register(nameof(Region),typeof(Geometry),typeof(TideTransition),
        new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty,value); }
    public BitmapSource? Snapshot { get => (BitmapSource?)GetValue(SnapshotProperty); set => SetValue(SnapshotProperty,value); }
    public Point Origin { get => (Point)GetValue(OriginProperty); set => SetValue(OriginProperty,value); }
    public Brush? WaveBrush { get => (Brush?)GetValue(WaveBrushProperty); set => SetValue(WaveBrushProperty,value); }
    public Geometry? Region { get => (Geometry?)GetValue(RegionProperty); set => SetValue(RegionProperty,value); }
    protected override void OnRender(DrawingContext draw)
    {
        base.OnRender(draw);
        if (Snapshot is null || ActualWidth <= 0 || ActualHeight <= 0 || Progress >= 1) return;
        var bounds=new Rect(0,0,ActualWidth,ActualHeight);
        draw.PushClip(Region ?? new RectangleGeometry(bounds));
        if (Progress <= 0) { draw.DrawImage(Snapshot,bounds); draw.Pop(); return; }
        var origin=new Point(Math.Clamp(Origin.X,0,ActualWidth),Math.Clamp(Origin.Y,0,ActualHeight));
        var farX=Math.Max(origin.X,ActualWidth-origin.X);
        var farY=Math.Max(origin.Y,ActualHeight-origin.Y);
        const double bandWidth=18;
        // Carry the entire circular edge past the farthest corner before completion.
        var radius=(Math.Sqrt(farX*farX+farY*farY)+bandWidth)*Progress;
        var wave=new EllipseGeometry(origin,radius,radius);
        wave.Freeze();
        var remaining=new CombinedGeometry(GeometryCombineMode.Exclude,new RectangleGeometry(bounds),wave);
        remaining.Freeze(); draw.PushClip(remaining); draw.DrawImage(Snapshot,bounds); draw.Pop();
        if (WaveBrush is SolidColorBrush color)
        {
            var transparent=color.Color with { A=0 }; var tint=color.Color with { A=24 };
            var edge=new RadialGradientBrush
            {
                Center=new(.5,.5),GradientOrigin=new(.5,.5),RadiusX=.5,RadiusY=.5,
                GradientStops=new GradientStopCollection
                {
                    new(transparent,0),new(transparent,Math.Max(0,1-bandWidth/radius)),
                    new(tint,Math.Max(0,1-bandWidth*.45/radius)),new(transparent,1)
                }
            };
            edge.Freeze(); draw.PushClip(new RectangleGeometry(bounds));
            draw.DrawEllipse(edge,null,origin,radius,radius); draw.Pop();
        }
        draw.Pop();
    }
}
