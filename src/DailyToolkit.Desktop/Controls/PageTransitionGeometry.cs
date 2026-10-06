using System.Windows;
using System.Windows.Media;

namespace DailyToolkit.Desktop.Controls;

internal static class PageTransitionGeometry
{
    internal static StreamGeometry Create(bool gear,Transform transform)
    {
        var geometry=new StreamGeometry { Transform=transform };
        using var shape=geometry.Open();
        var count=gear ? 32 : 10;
        for (var i=0;i<count;i++)
        {
            var radius=gear ? (i%4 is 1 or 2 ? 100 : 78) : (i%2 == 0 ? 100 : 45);
            var angle=-Math.PI/2+i*2*Math.PI/count;
            var point=new Point(Math.Cos(angle)*radius,Math.Sin(angle)*radius);
            if (i == 0) shape.BeginFigure(point,isFilled:true,isClosed:true);
            else shape.LineTo(point,isStroked:true,isSmoothJoin:false);
        }
        return geometry;
    }

    internal static double CoverScale(Size size,Point center,bool gear)
    {
        var x=Math.Max(center.X,size.Width-center.X);
        var y=Math.Max(center.Y,size.Height-center.Y);
        // Both silhouettes contain this disk, so rotation cannot uncover a window corner.
        return (Math.Sqrt(x*x+y*y)+2)/(gear ? 70 : 40);
    }
}
