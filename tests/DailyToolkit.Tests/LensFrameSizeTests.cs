using DailyToolkit.Core.Gaming;

namespace DailyToolkit.Tests;

internal static partial class Program
{
    private static void RegisterFrameSizeTests()
    {
        foreach (var screen in new[] { new PixelBounds(0,0,1920,1080),new(-3840,-600,3840,2160),new(0,-1920,1080,1920),new(0,0,7680,4320),new(0,0,1366,768) })
        foreach (var preset in new[] { LensFrameSizePreset.Small,LensFrameSizePreset.Medium,LensFrameSizePreset.Large })
            Test($"Proportional {preset} frame fits {screen.Width}x{screen.Height} with each aspect ratio",() =>
            {
                var ratio=preset == LensFrameSizePreset.Small ? .2 : preset == LensFrameSizePreset.Medium ? .3 : .4;
                foreach (var aspect in Enum.GetValues<LensAspectRatio>())
                {
                    var (w,h)=LensFrameSize.Resolve(screen,640,384,preset,aspect);
                    var shape=aspect switch { LensAspectRatio.Square => 1d,LensAspectRatio.Wide => 16d/9,
                        LensAspectRatio.Standard => 4d/3,LensAspectRatio.Ultrawide => 21d/9,_ => screen.Width/(double)screen.Height };
                    Require(w > 0 && h > 0 && w <= Math.Round(screen.Width*ratio) && h <= Math.Round(screen.Height*ratio));
                    Require(Math.Abs(w-h*shape) <= 2,"Pixel rounding changed the selected aspect ratio");
                }
            });
        Test("Custom sizes preserve independently requested dimensions across differing monitors",() =>
        {
            Require(LensFrameSize.Resolve(new(0,0,1920,1080),800,240,LensFrameSizePreset.Custom,LensAspectRatio.Square) == (800,240));
            Require(LensFrameSize.Resolve(new(0,0,640,480),800,600,LensFrameSizePreset.Custom) == (640,480));
            Require(LensFrameSize.Resolve(new(0,0,3840,2160),800,600,LensFrameSizePreset.Custom) == (800,600));
        });
        foreach (var vertical in new[] { false,true })
        foreach (var aspect in Enum.GetValues<LensAspectRatio>())
            Test($"{(vertical ? "Vertical" : "Horizontal")} preset relay uses destination size and preserves {aspect}",() =>
            {
                PixelBounds[] monitors=vertical ? [new(0,0,1920,1080),new(0,1080,1080,1920)] :
                    [new(-1920,0,1920,1080),new(0,0,3840,2160)];
                var a=monitors[0]; var b=monitors[1];
                var movement=new LensMovement(monitors,0,640,384,4,a.Left+a.Width/2d,a.Top+a.Height/2d,true,
                    sizePreset:LensFrameSizePreset.Medium,aspect:aspect);
                var previousX=movement.State.X.OutputCenter; var previousY=movement.State.Y.OutputCenter;
                LensMovementState Move(double x,double y)
                { var s=movement.Move(x-previousX,y-previousY,x,y); previousX=x; previousY=y; return s; }
                var output=movement.State.Layout.Output;
                Move(vertical ? 500 : a.Left+a.Width-output.Width/2d,vertical ? a.Top+a.Height-output.Height/2d : 500);
                var relay=movement.State.Relay ?? throw new InvalidOperationException("No destination relay formed");
                var expected=LensFrameSize.Resolve(b,640,384,LensFrameSizePreset.Medium,aspect);
                Require(relay.VirtualOutput.Width == expected.Width && relay.VirtualOutput.Height == expected.Height);
                Move(vertical ? previousX : b.Left+1,vertical ? b.Top+1 : previousY);
                Require(movement.State.MonitorIndex == 0,"Mouse entry switched before the virtual center axis");
                Move(vertical ? previousX : relay.VirtualOutput.Left+expected.Width/2d,
                    vertical ? relay.VirtualOutput.Top+expected.Height/2d : previousY);
                var landed=movement.State;
                Require(landed.MonitorIndex == 1 && landed.Layout.Output.Width == expected.Width && landed.Layout.Output.Height == expected.Height);
                Require(landed.Layout.Output.Left >= b.Left && landed.Layout.Output.Top >= b.Top &&
                    landed.Layout.Output.Left+expected.Width <= b.Left+b.Width && landed.Layout.Output.Top+expected.Height <= b.Top+b.Height);
                Require(landed.Layout.Source.Left >= 0 && landed.Layout.Source.Top >= 0 &&
                    landed.Layout.Source.Left+landed.Layout.Source.Width <= b.Width && landed.Layout.Source.Top+landed.Layout.Source.Height <= b.Height);
                movement.SetMovable(false); Require(Move(previousX+99,previousY+99) == landed,"Fixed preset frame moved");
            });
        Test("Unknown size and aspect preferences are rejected while older preferences stay enabled",() =>
        {
            Require(new GamingPreferences().IsValid && new GamingPreferences().IsEnabled);
            Require(!(new GamingPreferences { FrameSizePreset=(LensFrameSizePreset)99 }).IsValid);
            Require(!(new GamingPreferences { AspectRatio=(LensAspectRatio)99 }).IsValid);
        });
        Test("Odd preset widths relay at the first physical pixel beyond their half-pixel center axis",() =>
        {
            var movement=new LensMovement([new(0,0,1920,1080),new(1920,0,1366,768)],0,640,384,4,960,540,true,
                sizePreset:LensFrameSizePreset.Small);
            var x=960d;
            void Move(double next) { movement.Move(next-x,0,next,540); x=next; }
            Move(1728); var output=movement.State.Relay!.VirtualOutput;
            Require(output.Width == 273);
            var axis=output.Left+output.Width/2d;
            Move(1921); Move(Math.Floor(axis)); Require(movement.State.MonitorIndex == 0);
            Move(Math.Ceiling(axis)); Require(movement.State.MonitorIndex == 1 && movement.State.Layout.Output.Width == 273);
        });
    }
}
