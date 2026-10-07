using DailyToolkit.Core.Gaming;

namespace DailyToolkit.Tests;

internal static partial class Program
{
    private static void RegisterLensMovementTests()
    {
        LensAxisMotion Axis(double output=500,double source=500) => LensAxisMotion.Create(output,source,100,900,25,975);
        LensAxisMotion Move(LensAxisMotion axis,double delta) => axis.Move(delta,100,900,25,975);
        Test("Coupled motion applies the exact mouse delta to output and source",() =>
        {
            var result=Move(Axis(),37);
            Require(result == new LensAxisMotion(537,537,LensAxisCoupling.Coupled));
        });
        foreach (var (name,delta,center,state) in new[] {
            ("Left",-400d,100d,LensAxisCoupling.MinDetached),("Right",400d,900d,LensAxisCoupling.MaxDetached),
            ("Top",-400d,100d,LensAxisCoupling.MinDetached),("Bottom",400d,900d,LensAxisCoupling.MaxDetached) })
        {
            Test($"{name} contact immediately detaches its axis without requiring more outward movement",() =>
            {
                var result=Move(Axis(),delta);
                Require(result == new LensAxisMotion(center,center,state));
            });
        }
        foreach (var (name,output,source,delta) in new[] {
            ("Left",100d,94d,6d),("Right",900d,906d,-6d),("Top",100d,94d,6d),("Bottom",900d,906d,-6d) })
        {
            Test($"{name} detached source rejoins exactly at the output center axis",() =>
            {
                var result=Move(Axis(output,source),delta);
                Require(result == new LensAxisMotion(output,output,LensAxisCoupling.Coupled));
            });
        }
        Test("Rejoining consumes six pixels and applies the remaining fourteen to both centers",() =>
        {
            Require(Move(Axis(100,94),20) == new LensAxisMotion(114,114,LensAxisCoupling.Coupled));
            Require(Move(Axis(900,906),-20) == new LensAxisMotion(886,886,LensAxisCoupling.Coupled));
        });
        Test("One large delta rejoins, moves to the opposite edge, detaches and uses its remainder",() =>
        {
            Require(Move(Axis(100,94),826) == new LensAxisMotion(900,920,LensAxisCoupling.MaxDetached));
            Require(Move(Axis(900,906),-826) == new LensAxisMotion(100,80,LensAxisCoupling.MinDetached));
        });
        Test("Independent axes combine normally at corners without a special corner state",() =>
        {
            var x=Move(Axis(100,80),-30); var y=Move(Axis(),40);
            Require(x == new LensAxisMotion(100,50,LensAxisCoupling.MinDetached));
            Require(y == new LensAxisMotion(540,540,LensAxisCoupling.Coupled));
            y=Move(y,-470);
            Require(y == new LensAxisMotion(100,70,LensAxisCoupling.MinDetached) && x.SourceCenter == 50);
        });
        Test("Source limits stop outward motion and reverse immediately without hidden overshoot",() =>
        {
            var left=Move(Axis(),-5000); var right=Move(Axis(),5000);
            Require(left == new LensAxisMotion(100,25,LensAxisCoupling.MinDetached));
            Require(right == new LensAxisMotion(900,975,LensAxisCoupling.MaxDetached));
            Require(Move(left,5).SourceCenter == 30 && Move(right,-5).SourceCenter == 970);
        });
        Test("Axis motion supports negative monitor origins and a frame filling the monitor",() =>
        {
            var axis=LensAxisMotion.Create(-1000,-1000,-1800,-200,-1875,-125);
            Require(axis.Move(-900,-1800,-200,-1875,-125) == new LensAxisMotion(-1800,-1875,LensAxisCoupling.MinDetached));
            var full=LensAxisMotion.Create(100,100,100,100,50,150);
            Require(full.Move(80,100,100,50,150) == new LensAxisMotion(100,150,LensAxisCoupling.MaxDetached));
        });
        Test("Two-dimensional source and output remain bounded through corners and large reversals",() =>
        {
            PixelBounds monitor=new(-1920,-200,1920,1080);
            var movement=new LensMovement([monitor],0,640,384,2,-960,340,true);
            var random=new Random(61);
            for (var step=0;step<500;step++)
            {
                var state=movement.Move(random.Next(-3000,3001),random.Next(-3000,3001),0,0);
                var output=state.Layout.Output; var source=state.Layout.Source;
                Require(output.Left >= monitor.Left && output.Top >= monitor.Top &&
                    output.Left+output.Width <= monitor.Left+monitor.Width && output.Top+output.Height <= monitor.Top+monitor.Height);
                Require(source.Left >= 0 && source.Top >= 0 && source.Left+source.Width <= monitor.Width && source.Top+source.Height <= monitor.Height);
                foreach (var axis in new[] {state.X,state.Y})
                    Require(axis.Coupling == LensAxisCoupling.Coupled ? axis.SourceCenter == axis.OutputCenter :
                        axis.Coupling == LensAxisCoupling.MinDetached ? axis.SourceCenter <= axis.OutputCenter : axis.SourceCenter >= axis.OutputCenter);
            }
        });
        Test("Fixed mode freezes detached centers and source area; movable resumes without recentering",() =>
        {
            var movement=new LensMovement([new(0,0,1000,1000)],0,200,200,4,500,500,true);
            movement.Move(-406,-450,0,0);
            var frozen=movement.State;
            Require(frozen.X.SourceCenter == 94 && frozen.X.OutputCenter == 100 && frozen.Y.SourceCenter == 50);
            movement.SetMovable(false);
            Require(ReferenceEquals(movement.Move(-1000,800,-500,2000),frozen) && ReferenceEquals(movement.State,frozen));
            movement.SetMovable(true);
            Require(ReferenceEquals(movement.State,frozen));
            var resumed=movement.Move(20,0,0,0);
            Require(resumed.X == new LensAxisMotion(114,114,LensAxisCoupling.Coupled) && resumed.Y == frozen.Y);
        });
        Test("Odd frame dimensions and changing zoom preserve coupled centers and valid source bounds",() =>
        {
            var movement=new LensMovement([new(-401,-203,801,607)],0,201,151,10,0,100,true);
            movement.Move(16,-29,0,0);
            var state=movement.State;
            Require(state.X.OutputCenter == state.Layout.Output.Left+state.Layout.Output.Width/2d &&
                state.Y.OutputCenter == state.Layout.Output.Top+state.Layout.Output.Height/2d &&
                state.X.SourceCenter == state.X.OutputCenter && state.Y.SourceCenter == state.Y.OutputCenter);
            movement.Move(-500,-500,0,0); movement.UpdateZoom(1);
            Require(movement.State.Layout.Source.Left >= 0 && movement.State.Layout.Source.Top >= 0 &&
                movement.State.Layout.Source.Width == 201 && movement.State.Layout.Source.Height == 151);
        });
        foreach (var (name,target,dx,dy,mx,my) in new[] {
            ("Left",new PixelBounds(-1000,0,1000,1000),-600d,0d,-100d,500d),
            ("Right",new PixelBounds(1000,0,1000,1000),600d,0d,1100d,500d),
            ("Top",new PixelBounds(0,-1000,1000,1000),0d,-600d,500d,-100d),
            ("Bottom",new PixelBounds(0,1000,1000,1000),0d,600d,500d,1100d) })
        {
            Test($"{name} relay waits for the mouse to reach the correct mirrored virtual axis",() =>
            {
                var movement=new LensMovement([new(0,0,1000,1000),target],0,200,200,4,500,500,true);
                var contact=movement.Move(dx*2/3,dy*2/3,500+dx*2/3,500+dy*2/3);
                Require(contact.MonitorIndex == 0 && contact.Relay is not null);
                var entered=movement.Move(dx*0.168,dy*0.168,500+dx*(2d/3+0.168),500+dy*(2d/3+0.168));
                Require(entered.MonitorIndex == 0,"Mouse entry changed the capture monitor before the virtual axis");
                var state=movement.Move(mx-(500+dx*(2d/3+0.168)),my-(500+dy*(2d/3+0.168)),mx,my);
                Require(state.MonitorIndex == 1 && state.X.Coupling == LensAxisCoupling.Coupled && state.Y.Coupling == LensAxisCoupling.Coupled);
                Require(state.X.OutputCenter == mx && state.Y.OutputCenter == my && state.X.SourceCenter == mx && state.Y.SourceCenter == my);
            });
        }
        Test("Mouse motion on the wrong virtual axis and saturated source motion never switch monitors",() =>
        {
            var movement=new LensMovement([new(0,0,1000,1000),new(-1000,0,1000,1000)],0,200,200,4,500,500,true);
            movement.Move(-500,0,0,450);
            var state=movement.Move(-50,50,-50,500);
            Require(state.MonitorIndex == 0 && state.X.SourceCenter == 25 && state.Y.SourceCenter == state.Y.OutputCenter);
            Require(state.Relay?.VirtualOutput.Left == -200 && state.Relay.VirtualOutput.Top == state.Layout.Output.Top);
            state=movement.Move(0,80,-50,580);
            Require(state.MonitorIndex == 0,"The horizontal virtual X axis incorrectly triggered a relay");
        });
        foreach (var (name,center,mouse,expected) in new[] {
            ("top",100d,60d,LensAxisCoupling.MinDetached),("bottom",900d,940d,LensAxisCoupling.MaxDetached) })
        {
            Test($"Horizontal relay at target {name} uses mouse Y and then ordinary detached return",() =>
            {
                var movement=new LensMovement([new(0,0,1000,1000),new(-1000,0,1000,1000)],0,200,200,4,500,center,true);
                var state=movement.Move(-600,0,-100,mouse);
                Require(state.MonitorIndex == 1 && state.X.Coupling == LensAxisCoupling.Coupled && state.Y == new LensAxisMotion(center,mouse,expected));
                state=movement.Move(0,center-mouse+Math.Sign(center-mouse)*14,-100,center+Math.Sign(center-mouse)*14);
                Require(state.Y == new LensAxisMotion(center+Math.Sign(center-mouse)*14,center+Math.Sign(center-mouse)*14,LensAxisCoupling.Coupled));
            });
        }
        foreach (var (name,center,mouse,expected) in new[] {
            ("left",100d,60d,LensAxisCoupling.MinDetached),("right",900d,940d,LensAxisCoupling.MaxDetached) })
        {
            Test($"Vertical relay at target {name} uses mouse X and then ordinary detached return",() =>
            {
                var movement=new LensMovement([new(0,0,1000,1000),new(0,-1000,1000,1000)],0,200,200,4,center,500,true);
                var state=movement.Move(0,-600,mouse,-100);
                Require(state.MonitorIndex == 1 && state.Y.Coupling == LensAxisCoupling.Coupled && state.X == new LensAxisMotion(center,mouse,expected));
                state=movement.Move(center-mouse+Math.Sign(center-mouse)*14,0,center+Math.Sign(center-mouse)*14,-100);
                Require(state.X == new LensAxisMotion(center+Math.Sign(center-mouse)*14,center+Math.Sign(center-mouse)*14,LensAxisCoupling.Coupled));
            });
        }
        Test("Relay mirrors the frame rather than the grip and centers a perpendicular interior axis",() =>
        {
            var horizontal=new LensMovement([new(0,0,1000,1000),new(-1000,0,1000,1000)],0,200,200,4,500,500,true);
            var state=horizontal.Move(-660,0,-100,560);
            Require(state.MonitorIndex == 1 && state.Y == new LensAxisMotion(500,500,LensAxisCoupling.Coupled));
            var vertical=new LensMovement([new(0,0,1000,1000),new(0,-1000,1000,1000)],0,200,200,4,500,500,true);
            state=vertical.Move(0,-660,560,-100);
            Require(state.MonitorIndex == 1 && state.X == new LensAxisMotion(500,500,LensAxisCoupling.Coupled));
        });
        Test("Opposite-half mouse at a target boundary is limited to the center and remains coupled",() =>
        {
            foreach (var center in new[] {100d,900d})
            {
                var mouse=center == 100 ? 140 : 860;
                var horizontal=new LensMovement([new(0,0,1000,1000),new(-1000,0,1000,1000)],0,200,200,4,500,center,true);
                Require(horizontal.Move(-600,0,-100,mouse).Y == new LensAxisMotion(center,center,LensAxisCoupling.Coupled));
                var vertical=new LensMovement([new(0,0,1000,1000),new(0,-1000,1000,1000)],0,200,200,4,center,500,true);
                Require(vertical.Move(0,-600,mouse,-100).X == new LensAxisMotion(center,center,LensAxisCoupling.Coupled));
            }
        });
        Test("Fixed freezes a pending virtual relay and skips even invalid movement input",() =>
        {
            var movement=new LensMovement([new(0,0,1000,1000),new(-1000,0,1000,1000)],0,200,200,4,500,500,true);
            movement.Move(-500,0,0,500); var frozen=movement.State;
            Require(frozen.Relay is not null);
            movement.SetMovable(false);
            Require(ReferenceEquals(movement.Move(double.NaN,double.PositiveInfinity,-200,500),frozen));
            movement.SetMovable(true);
            Require(ReferenceEquals(movement.State,frozen));
            Require(movement.Move(-100,0,-100,500).MonitorIndex == 1);
        });
        Test("A smaller offset monitor clamps the mirror legally and returning restores requested dimensions",() =>
        {
            var movement=new LensMovement([new(0,0,1000,1000),new(-300,200,300,400)],0,500,600,4,500,300,true);
            var state=movement.Move(-650,0,-150,250);
            Require(state.MonitorIndex == 1 && state.Layout.Output == new PixelBounds(-300,200,300,400));
            Require(state.Y == new LensAxisMotion(400,250,LensAxisCoupling.MinDetached) && state.Layout.Source.Top == 0);
            state=movement.Move(400,0,250,250);
            Require(state.MonitorIndex == 0 && state.Layout.Output.Width == 500 && state.Layout.Output.Height == 600);
        });
        Test("Desktop gaps and negative origins use reflection across the two facing monitor edges",() =>
        {
            var movement=new LensMovement([new(-2000,-200,1000,1000),new(-900,-200,1000,1000)],0,200,200,4,-1500,300,true);
            var state=movement.Move(700,0,-800,300);
            Require(state.MonitorIndex == 1 && state.X.OutputCenter == -800 && state.Layout.Output.Left == -900);
        });
        Test("A large mouse event consumes the remaining displacement after one or several relays",() =>
        {
            var movement=new LensMovement([new(0,0,1000,1000),new(1000,0,1000,1000),new(2000,0,1000,1000)],0,200,200,4,500,500,true);
            var state=movement.Move(1800,0,2300,500);
            Require(state.MonitorIndex == 2 && state.X == new LensAxisMotion(2300,2300,LensAxisCoupling.Coupled));
        });
        Test("A virtual axis crossed before the output reaches its edge cannot trigger a handoff",() =>
        {
            var movement=new LensMovement([new(0,0,1000,1000),new(-1000,0,1000,1000)],0,200,200,4,500,500,true);
            var state=movement.Move(-500,0,-300,500);
            Require(state.MonitorIndex == 0 && state.X.OutputCenter == 100);
        });
        Test("Center snap accumulates small deltas, releases, and freezes the actual visible frame",() =>
        {
            var movement=new LensMovement([new(0,0,1000,1000)],0,200,200,4,500,500,true,centerSnap:true);
            for (var i=1;i<=24;i++) Require(movement.Move(1,0,500+i,500).X.OutputCenter == 500);
            Require(movement.Move(1,0,525,500).X.OutputCenter == 525);
            movement.Move(-15,0,510,500); movement.SetMovable(false);
            Require(movement.State.X == new LensAxisMotion(500,500,LensAxisCoupling.Coupled));
            movement.SetMovable(true); movement.BeginDrag();
            Require(movement.Move(25,0,535,500).X.OutputCenter == 525);
        });
        Test("Virtual reflection uses the visible snapped center rather than accumulated snap offsets",() =>
        {
            var movement=new LensMovement([new(0,0,1000,1000),new(-1000,0,1000,1000)],0,200,200,4,500,500,true,centerSnap:true);
            var state=movement.Move(-400,10,100,510);
            Require(state.Y.OutputCenter == 500 && state.Relay?.VirtualOutput.Top == state.Layout.Output.Top);
            state=movement.Move(-200,0,-100,510);
            Require(state.MonitorIndex == 1 && state.Y.OutputCenter == 500 && state.Y.SourceCenter == 500);
        });
    }
}
