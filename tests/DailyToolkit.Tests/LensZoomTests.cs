using DailyToolkit.Core.Gaming;

namespace DailyToolkit.Tests;

internal static partial class Program
{
    private static void RegisterZoomTests()
    {
        Test("Image choices normalize unknown/AI modes and unsafe sharpness without loading AI", () =>
        {
            Require(new GamingPreferences().LensImageMode == LensImageMode.Clear);
            Require(LensImageSettings.NormalizeMode((LensImageMode)99) == LensImageMode.Clear);
            Require(LensImageSettings.NormalizeMode(LensImageMode.AIEnhanced) == LensImageMode.HighQuality);
            foreach(var value in new[] {-5d,0d,35.4,101d,double.NaN,double.PositiveInfinity})
                Require((new GamingPreferences {Sharpness=value}).Normalize().Sharpness == 100);
        });
        foreach (var (input,expected) in new[] { (2d,2d),(1d,1d),(16d,16d),(1.24,1.25),(1.26,1.25),
            (3.249999,3.25),(double.NaN,2d),(double.PositiveInfinity,2d),(double.NegativeInfinity,2d),(-4d,1d),(20d,16d) })
        {
            Test($"Quarter zoom normalization: {input} -> {expected}", () =>
            {
                Require(LensZoom.Normalize(input) == expected);
                Require(LensZoom.Normalize(expected) == expected);
                Require(LensZoom.Format(input).EndsWith("×") && !LensZoom.Format(input).Contains("249999"));
            });
        }
        Test("Precision wheel accumulates signed sub-ticks and consumes every full tick", () =>
        {
            var wheel=new LensWheelAccumulator();
            Require(wheel.Consume(120) == 1 && wheel.Consume(-120) == -1);
            Require(wheel.Consume(40) == 0 && wheel.Consume(40) == 0 && wheel.Consume(40) == 1);
            Require(wheel.Consume(90) == 0 && wheel.Consume(-30) == 0 && wheel.Consume(-180) == -1);
            Require(wheel.Consume(12000) == 100);
            wheel.Consume(119); wheel.Reset(); Require(wheel.Consume(1) == 0);
        });
        Test("Zoom HUD reuses state, extends visibility, fades and clears", () =>
        {
            var hud=new LensZoomHud(); Require(hud.Opacity(0) == 0);
            hud.Show(3.2499,10); Require(hud.Zoom == 3.25 && hud.Opacity(10.6) == 1);
            Require(hud.Opacity(10.725) is > 0.49f and < 0.51f);
            hud.Show(16,10.75); Require(hud.Zoom == 16 && hud.Opacity(11.3) == 1);
            Require(hud.Opacity(11.6) == 0); hud.Clear(); Require(hud.Opacity(12) == 0);
        });
        Test("12x, 14x and 16x stay bounded at every corner, negative origin and tiny monitor", () =>
        {
            foreach(var zoom in new[] {12d,14d,16d})
            foreach(var monitor in new[] {new PixelBounds(-1920,-240,1920,1080),new PixelBounds(2560,0,1280,720),new PixelBounds(-201,90,201,101)})
            foreach(var (dx,dy) in new[] {(-5000,-5000),(-5000,5000),(5000,-5000),(5000,5000),(0,0)})
            {
                var layout=LensLayout.Calculate(monitor,640,384,zoom,monitor.Left+dx,monitor.Top+dy);
                Require(layout.Source.Left >= 0 && layout.Source.Top >= 0 &&
                    layout.Source.Left+layout.Source.Width <= monitor.Width && layout.Source.Top+layout.Source.Height <= monitor.Height);
                var movement=new LensMovement([monitor],0,640,384,zoom,monitor.Left+monitor.Width/2d,monitor.Top+monitor.Height/2d,true);
                movement.BeginDrag(); var state=movement.Move(dx,dy,monitor.Left+dx,monitor.Top+dy);
                Require(state.Layout.Source.Left >= 0 && state.Layout.Source.Top >= 0 &&
                    state.Layout.Source.Left+state.Layout.Source.Width <= monitor.Width && state.Layout.Source.Top+state.Layout.Source.Height <= monitor.Height);
            }
        });
        Test("12x, 14x and 16x retain mirrored virtual-axis relay in all four directions", () =>
        {
            foreach(var zoom in new[]{12d,14d,16d})
            foreach(var (target,dx,dy,mx,my) in new[] {
                (new PixelBounds(-1000,0,1000,1000),-600d,0d,-100d,500d),
                (new PixelBounds(1000,0,1000,1000),600d,0d,1100d,500d),
                (new PixelBounds(0,-1000,1000,1000),0d,-600d,500d,-100d),
                (new PixelBounds(0,1000,1000,1000),0d,600d,500d,1100d)})
            {
                var movement=new LensMovement([new(0,0,1000,1000),target],0,200,200,zoom,500,500,true);
                var contact=movement.Move(dx*2/3,dy*2/3,500+dx*2/3,500+dy*2/3);
                Require(contact.MonitorIndex==0 && contact.Relay is not null);
                var entered=movement.Move(dx*0.168,dy*0.168,500+dx*(2d/3+0.168),500+dy*(2d/3+0.168));
                Require(entered.MonitorIndex==0);
                var state=movement.Move(mx-(500+dx*(2d/3+0.168)),my-(500+dy*(2d/3+0.168)),mx,my);
                Require(state.MonitorIndex==1 && state.X.Coupling==LensAxisCoupling.Coupled && state.Y.Coupling==LensAxisCoupling.Coupled);
                Require(state.Layout.Source.Left>=0 && state.Layout.Source.Top>=0);
            }
        });
    }
}
