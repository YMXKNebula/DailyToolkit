using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    // Optional interactive-desktop check. Pixels stay in memory; no desktop images,
    // preferences, cursor input or Windows startup configuration are written.
    private static async Task CheckLiveLensMovementAsync()
    {
        var monitors=LensNativeWindow.Monitors();
        Require(monitors.Count > 0,"No real monitor is available for the live capture check");
        var monitor=monitors[0]; var bounds=monitor.Bounds;
        var canvas=new System.Windows.Controls.Grid { Background=new SolidColorBrush(Color.FromRgb(32,64,160)) };
        var scene=new Window
        {
            Content=canvas,Width=400,Height=300,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
            ShowInTaskbar=false,ShowActivated=false,Topmost=true,Left=-20000,Top=-20000
        };
        using var session=new ScreenLensSession(nativePointer:false);
        var status=""; session.StatusChanged+=value => status=value;
        try
        {
            scene.Show();
            Require(SetWindowPos(new WindowInteropHelper(scene).Handle,new IntPtr(-1),bounds.Left,bounds.Top,bounds.Width,bounds.Height,0x10),
                "The test scene did not cover the sampled monitor");
            await Task.Delay(120);
            session.SetMovement(true);
            session.Start([monitor],monitor,200,160,4,0.35,0);
            await WaitFor(() => session.IsVisible,"Live lens did not start: "+status);
            var initial=session.ActiveBounds!; var pointer=session.PointerForDiagnostics!;
            var gripX=initial.Left+initial.Width/2; var gripY=initial.Top+initial.Height/2;
            Require(pointer.Process(0x201,gripX,gripY),"Test drag did not begin");
            var sourceBefore=session.CaptureForDiagnostics!.SourceForDiagnostics.Source;
            for (var i=1;i<=80;i++) pointer.Process(0x200,gripX+i,gripY);
            Require(session.ActiveBounds == initial && session.CaptureForDiagnostics.SourceForDiagnostics.Source == sourceBefore,
                "Mouse input synchronously ran native window or render work before returning");
            await WaitFor(() => session.MovementForDiagnostics?.X.OutputCenter == gripX+80,
                "Coalescing a burst dropped mouse deltas or kept an older position");
            pointer.Process(0x200,gripX,gripY);
            await WaitFor(() => session.ActiveBounds == initial,"The coalesced drag could not return to its original position");
            var mouseX=bounds.Left+initial.Width/2-6;
            Require(pointer.Process(0x200,mouseX,gripY),"Test edge drag was ignored");
            await WaitFor(() => session.MovementForDiagnostics?.X.SourceCenter == bounds.Left+initial.Width/2d-6,
                "The queued edge sample did not reach its latest location");
            var detached=session.MovementForDiagnostics!;
            Require(detached.X.Coupling == LensAxisCoupling.MinDetached && detached.X.SourceCenter == detached.X.OutputCenter-6,
                "The real session recentered the detached sampling area");
            var capture=session.CaptureForDiagnostics!; var handle=session.LensHandle;
            var source=capture.SourceForDiagnostics.Source;
            pointer.Process(0x200,mouseX-10,gripY);
            session.SetMovement(false);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            var frozen=session.MovementForDiagnostics!;
            Require(frozen == detached && source == capture.SourceForDiagnostics.Source,"Fixed changed actual output or source");
            pointer.Process(0x200,bounds.Left-500,gripY);
            Require(session.MovementForDiagnostics == frozen && session.CaptureForDiagnostics == capture && session.LensHandle == handle,
                "Fixed movement recalculated positions or recreated runtime resources");
            for (var step=0;step<3;step++)
            {
                var before=capture.FramesRendered; var red=(byte)(80+step*20);
                canvas.Background=new SolidColorBrush(Color.FromRgb(red,64,160));
                await WaitFor(() =>
                {
                    var pixels=capture.ReadOutput(); var center=(80*200+100)*4;
                    return capture.FramesRendered > before && Math.Abs(pixels[center]-160)<=2 &&
                        Math.Abs(pixels[center+1]-64)<=2 && Math.Abs(pixels[center+2]-red)<=2;
                },"Frozen output did not refresh the live Windows capture");
                Require(session.MovementForDiagnostics == frozen && capture.SourceForDiagnostics.Source == source,
                    "Live fixed frames changed the frozen sampling geometry");
            }
            session.SetMovement(true);
            Require(session.MovementForDiagnostics == frozen,"Returning to movable recentered sampling");
            var actual=session.ActiveBounds!;
            gripX=actual.Left+actual.Width/2; gripY=actual.Top+actual.Height/2;
            pointer.Process(0x201,gripX,gripY); pointer.Process(0x200,gripX+20,gripY);
            await WaitFor(() => session.MovementForDiagnostics!.X.OutputCenter == frozen.X.OutputCenter+14,
                "The resumed drag position was not applied");
            Require(session.MovementForDiagnostics!.X == new LensAxisMotion(frozen.X.OutputCenter+14,frozen.X.OutputCenter+14,LensAxisCoupling.Coupled),
                "The real pointer/session lost the remaining fourteen pixels after rejoining");
            pointer.Process(0x202,gripX+20,gripY);
            pointer.Process(0x201,gripX+20,gripY); pointer.Process(0x200,gripX+30,gripY);
            session.Stop();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Require(!session.HasCaptureResources,"The live session retained native capture resources");
            Console.WriteLine("PASS Live Windows capture updates frozen detached sampling; resume consumes the remaining delta and releases runtime resources");
            Console.WriteLine("PASS Mouse bursts return before native/render work, preserve every delta, and apply only the latest queued position");
        }
        finally { session.Stop(); scene.Close(); }

        await CheckLiveRelayAsync();

        static async Task WaitFor(Func<bool> condition,string message)
        {
            var deadline=DateTime.UtcNow.AddSeconds(6);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(30);
            Require(condition(),message);
        }
    }

    private static async Task CheckLiveRelayAsync()
    {
        var monitors=LensNativeWindow.Monitors(); var origin=monitors[0];
        var target=monitors.Skip(1).FirstOrDefault(m =>
            ((m.Bounds.Left+m.Bounds.Width <= origin.Bounds.Left || m.Bounds.Left >= origin.Bounds.Left+origin.Bounds.Width) &&
             m.Bounds.Top < origin.Bounds.Top+origin.Bounds.Height && m.Bounds.Top+m.Bounds.Height > origin.Bounds.Top) ||
            ((m.Bounds.Top+m.Bounds.Height <= origin.Bounds.Top || m.Bounds.Top >= origin.Bounds.Top+origin.Bounds.Height) &&
             m.Bounds.Left < origin.Bounds.Left+origin.Bounds.Width && m.Bounds.Left+m.Bounds.Width > origin.Bounds.Left));
        if (target is null) { Console.WriteLine("SKIP Live relay requires two side-adjacent monitors"); return; }
        var a=origin.Bounds; var b=target.Bounds;
        var horizontal=b.Left+b.Width <= a.Left || b.Left >= a.Left+a.Width;
        var sign=horizontal ? (b.Left < a.Left ? -1 : 1) : (b.Top < a.Top ? -1 : 1);
        var scenes=new List<Window>();
        using var session=new ScreenLensSession(nativePointer:false); var switches=0;
        session.MonitorChanged+=_ => switches++;
        try
        {
            foreach (var (monitor,color) in new[] {(origin,Color.FromRgb(32,64,160)),(target,Color.FromRgb(40,140,70))})
            {
                var scene=new Window
                {
                    Background=new SolidColorBrush(color),Width=400,Height=300,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
                    ShowInTaskbar=false,ShowActivated=false,Topmost=true,Left=-20000,Top=-20000
                };
                scenes.Add(scene); scene.Show(); var bounds=monitor.Bounds;
                Require(SetWindowPos(new WindowInteropHelper(scene).Handle,new IntPtr(-1),bounds.Left,bounds.Top,bounds.Width,bounds.Height,0x10),
                    "A relay test scene did not cover its monitor");
            }
            await Task.Delay(120);
            session.SetMovement(true); session.Start([origin,target],origin,200,160,4,0.35,0);
            await WaitFor(() => session.IsVisible,"Live relay did not start");
            var pointer=session.PointerForDiagnostics!; var handle=session.LensHandle;
            var renderer=session.RendererForDiagnostics; var capture=session.CaptureForDiagnostics;
            var state=session.MovementForDiagnostics!;
            var x=(int)state.X.OutputCenter; var y=(int)state.Y.OutputCenter;
            pointer.Process(0x201,x,y);
            if (horizontal) y=(Math.Max(a.Top,b.Top)+Math.Min(a.Top+a.Height,b.Top+b.Height))/2;
            else x=(Math.Max(a.Left,b.Left)+Math.Min(a.Left+a.Width,b.Left+b.Width))/2;
            pointer.Process(0x200,x,y);
            if (horizontal) x=sign < 0 ? a.Left+100 : a.Left+a.Width-100;
            else y=sign < 0 ? a.Top+80 : a.Top+a.Height-80;
            pointer.Process(0x200,x,y);
            await WaitFor(() => session.MovementForDiagnostics?.Relay is { TargetMonitor:1 },"The queued edge did not form its virtual relay");
            state=session.MovementForDiagnostics!;
            Require(state.MonitorIndex == 0 && state.Relay is { TargetMonitor:1 } && switches == 0,
                "Edge contact did not create a geometry-only relay");
            var virtualBounds=state.Relay!.VirtualOutput;
            if (horizontal) x=sign < 0 ? b.Left+b.Width-1 : b.Left+1;
            else y=sign < 0 ? b.Top+b.Height-1 : b.Top+1;
            pointer.Process(0x200,x,y);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Require(session.MovementForDiagnostics!.MonitorIndex == 0 && switches == 0 &&
                session.CaptureForDiagnostics == capture && session.RendererForDiagnostics == renderer && session.LensHandle == handle,
                "Mouse entry or virtual geometry started another capture/renderer");
            var frozen=session.MovementForDiagnostics;
            if (horizontal) x=virtualBounds.Left+virtualBounds.Width/2;
            else y=virtualBounds.Top+virtualBounds.Height/2;
            pointer.Process(0x200,x,y); session.SetMovement(false);
            await Task.Delay(100);
            Require(session.MovementForDiagnostics == frozen && switches == 0 && session.CaptureForDiagnostics == capture,
                "An already queued relay moved the frozen actual output");
            session.SetMovement(true);
            state=session.MovementForDiagnostics!;
            x=(int)state.X.OutputCenter; y=(int)state.Y.OutputCenter;
            pointer.Process(0x201,x,y);
            if (horizontal) x=virtualBounds.Left+virtualBounds.Width/2;
            else y=virtualBounds.Top+virtualBounds.Height/2;
            pointer.Process(0x200,x,y);
            // Input arrives faster than the dispatcher handoff. Its remaining delta
            // must survive snapshot coalescing and still use the existing pointer.
            if (horizontal) x+=sign*17; else y+=sign*17;
            pointer.Process(0x200,x,y);
            await WaitFor(() => session.MovementForDiagnostics?.MonitorIndex == 1 && Matches(40,140,70),
                "The queued relay did not render the target scene");
            state=session.MovementForDiagnostics!;
            Require((horizontal ? state.X.OutputCenter : state.Y.OutputCenter) ==
                (horizontal ? virtualBounds.Left+virtualBounds.Width/2d : virtualBounds.Top+virtualBounds.Height/2d)+sign*17,
                "Coalescing lost the movement immediately following the virtual axis");
            Require(switches == 1 && session.CaptureForDiagnostics != capture && session.RendererForDiagnostics == renderer &&
                session.LensHandle == handle && session.PointerForDiagnostics == pointer,
                "Relay recreated a same-size renderer/window/pointer or switched twice");
            var returnAxis=horizontal ? (sign < 0 ? a.Left+100 : a.Left+a.Width-100) : (sign < 0 ? a.Top+80 : a.Top+a.Height-80);
            if (horizontal) x=returnAxis; else y=returnAxis;
            pointer.Process(0x200,x,y);
            await WaitFor(() => session.MovementForDiagnostics?.MonitorIndex == 0 && Matches(32,64,160),
                "A held drag failed to return across the mirrored virtual axis");
            var injected=false;
            void DuringSwitch(string _)
            {
                if (injected || session.MovementForDiagnostics?.MonitorIndex != 1) return;
                injected=true;
                if (horizontal) x=returnAxis; else y=returnAxis;
                pointer.Process(0x200,x,y);
            }
            session.StatusChanged+=DuringSwitch;
            try
            {
                if (horizontal) x=virtualBounds.Left+virtualBounds.Width/2;
                else y=virtualBounds.Top+virtualBounds.Height/2;
                pointer.Process(0x200,x,y);
                await WaitFor(() => injected && switches == 4 && session.MovementForDiagnostics?.MonitorIndex == 0 && Matches(32,64,160),
                    "A delta received during native capture handoff was lost");
            }
            finally { session.StatusChanged-=DuringSwitch; }
            pointer.Process(0x202,x,y); session.Stop();
            Require(!session.HasCaptureResources,"Relay check retained runtime resources");
            Console.WriteLine("PASS Real monitor relay waits for the virtual axis, cancels on fixed, preserves queued/reentrant deltas and reuses the native window, pointer and same-size renderer");
        }
        finally { session.Stop(); foreach (var scene in scenes) scene.Close(); }

        bool Matches(byte red,byte green,byte blue)
        {
            if (session.CaptureForDiagnostics is not { } capture || session.ActiveBounds is not { } output) return false;
            var pixels=capture.ReadOutput(); var at=(output.Height/2*output.Width+output.Width/2)*4;
            return Math.Abs(pixels[at]-blue)<=2 && Math.Abs(pixels[at+1]-green)<=2 && Math.Abs(pixels[at+2]-red)<=2;
        }
        static async Task WaitFor(Func<bool> condition,string message)
        {
            var deadline=DateTime.UtcNow.AddSeconds(6);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(30);
            Require(condition(),message);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll",SetLastError=true)]
    private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
