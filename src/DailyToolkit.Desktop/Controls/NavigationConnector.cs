using System.Windows;
using System.Windows.Media;

namespace DailyToolkit.Desktop.Controls;

public sealed class NavigationConnector : FrameworkElement
{
    public static readonly DependencyProperty FillProperty=DependencyProperty.Register(nameof(Fill),typeof(Brush),typeof(NavigationConnector),
        new FrameworkPropertyMetadata(null,FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush? Fill { get => (Brush?)GetValue(FillProperty); set => SetValue(FillProperty,value); }
    protected override void OnRender(DrawingContext draw)
    {
        base.OnRender(draw);
        var w=ActualWidth; var h=ActualHeight;
        if (w <= 0 || h <= 0) return;
        draw.DrawGeometry(Fill,null,CreateShape(new Size(w,h)));
    }
    internal static StreamGeometry CreateShape(Size size)
    {
        var w=size.Width; var h=size.Height; var r=Math.Min(8,Math.Min(w,h/3));
        var shape=new StreamGeometry();
        using (var path=shape.Open())
        {
            path.BeginFigure(new(0,r),true,true); path.LineTo(new(w-r,r),true,false);
            path.BezierTo(new(w-r*.45,r),new(w,r*.55),new(w,0),true,false);
            path.LineTo(new(w,h),true,false);
            path.BezierTo(new(w,h-r*.55),new(w-r*.45,h-r),new(w-r,h-r),true,false);
            path.LineTo(new(0,h-r),true,false);
        }
        shape.Freeze(); return shape;
    }
}
