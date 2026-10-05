using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DailyToolkit.Core.Environment;
using DailyToolkit.Core.Tools;
using DailyToolkit.Core.Gaming;


namespace DailyToolkit.Tests;

internal static partial class Program
{
    private static void RegisterLensTests()
    {
        Test("Lens geometry magnifies only the requested frame at native pixel coordinates", () =>
        {
            var layout=LensLayout.Calculate(new(0,0,2560,1600),640,384,2,1280,800);
            Require(layout.Output == new PixelBounds(960,608,640,384));
            Require(layout.Source == new SourceArea(1120,704,320,192));
            var same=LensLayout.Calculate(new(0,0,2560,1600),640,384,1,1280,800);
            Require(same.Source.Width == same.Output.Width && same.Source.Height == same.Output.Height);
            var ten=LensLayout.Calculate(new(0,0,2560,1600),640,384,10,1280,800);
            Require(ten.Source.Width == 64 && Math.Abs(ten.Source.Height-38.4)<0.001);
        });
        Test("Lens stays inside monitors including negative origins and edge-following", () =>
        {
            var monitor=new PixelBounds(-1920,-200,1920,1080);
            foreach(var point in new[] { (-4000,-900),(0,300),(5000,300),(-900,4000) })
            {
                var layout=LensLayout.Calculate(monitor,640,384,2,point.Item1,point.Item2);
                Require(layout.Output.Left>=monitor.Left && layout.Output.Top>=monitor.Top &&
                    layout.Output.Left+layout.Output.Width<=monitor.Left+monitor.Width && layout.Output.Top+layout.Output.Height<=monitor.Top+monitor.Height);
                Require(layout.Source.Left>=0 && layout.Source.Top>=0 &&
                    layout.Source.Left+layout.Source.Width<=monitor.Width && layout.Source.Top+layout.Source.Height<=monitor.Height);
            }
            var small=LensLayout.Calculate(new(0,0,200,100),640,384,2,0,0);
            Require(small.Output.Width == 200 && small.Output.Height == 100);
        });
        Test("Center assistance snaps each axis, releases outside its range and clamps negative-origin monitors", () =>
        {
            var monitor=new PixelBounds(-1920,-200,1920,1080);
            var centered=new PixelBounds(-1280,148,640,384);
            var both=LensPlacement.Snap(monitor,640,384,centered.Left+24,centered.Top-24);
            Require(both.Bounds == centered && both.VerticalGuide && both.HorizontalGuide);
            var one=LensPlacement.Snap(monitor,640,384,centered.Left+25,centered.Top+10);
            Require(one.Bounds.Left == centered.Left+25 && one.Bounds.Top == centered.Top && !one.VerticalGuide && one.HorizontalGuide);
            var free=LensPlacement.Snap(monitor,640,384,centered.Left+25,centered.Top-25);
            Require(!free.VerticalGuide && !free.HorizontalGuide && free.Bounds.Top == centered.Top-25);
            var edge=LensPlacement.Snap(monitor,640,384,-4000,5000);
            Require(edge.Bounds == new PixelBounds(-1920,496,640,384) && !edge.VerticalGuide && !edge.HorizontalGuide);
            var small=LensPlacement.Snap(new(0,0,201,101),640,384,200,300);
            Require(small.Bounds == new PixelBounds(0,0,201,101));
        });
        Test("Magnifier supports a single empty or custom shortcut and validated activation modes", () =>
        {
            Require(new GamingPreferences().MovementMode == LensMovementMode.Fixed);
            Require(new GamingPreferences().WheelZoomEnabled);
            Require(new GamingPreferences { ToggleShortcut=null,ActivationMode=LensActivationMode.Hold }.IsValid);
            var custom=new KeyboardShortcut(3,0x5A,"Ctrl + Alt + Z");
            Require(custom.IsValid && new GamingPreferences { ToggleShortcut=custom }.IsValid);
            Require(!new GamingPreferences { ActivationMode=(LensActivationMode)99 }.IsValid);
            Require(!new GamingPreferences { MovementMode=(LensMovementMode)99 }.IsValid);
            Require(!new KeyboardShortcut(0,0x10,"Shift").IsValid && !new KeyboardShortcut(0x100,0x5A,"Z").IsValid);
        });

        Test("Frame pacing follows high-rate input and does not halve capped output at timing jitter", () =>
        {
            var follow=new LensFramePacer(0,120000);
            var cap=new LensFramePacer(60,120000);
            var rendered=0;
            for (var frame=0;frame<240;frame++)
            {
                var tick=frame*1000+(frame%3-1)*8;
                Require(follow.ShouldRender(tick),"Follow mode dropped an incoming 120 Hz frame");
                if (cap.ShouldRender(tick)) rendered++;
            }
            Require(rendered is >= 119 and <= 121,$"60 Hz cap rendered {rendered} frames in two seconds");
            Require(cap.ShouldRender(1_000_000),"Pacing did not resume after a pause");
        });
    }
}
