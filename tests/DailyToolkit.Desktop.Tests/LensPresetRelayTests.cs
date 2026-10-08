using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckLivePresetRelayAsync(double zoom=4)
    {
        var monitors=LensNativeWindow.Monitors(); var origin=monitors[0];
        var target=monitors.Skip(1).FirstOrDefault(m =>
            ((m.Bounds.Left+m.Bounds.Width <= origin.Bounds.Left || m.Bounds.Left >= origin.Bounds.Left+origin.Bounds.Width) &&
             m.Bounds.Top < origin.Bounds.Top+origin.Bounds.Height && m.Bounds.Top+m.Bounds.Height > origin.Bounds.Top) ||
            ((m.Bounds.Top+m.Bounds.Height <= origin.Bounds.Top || m.Bounds.Top >= origin.Bounds.Top+origin.Bounds.Height) &&
             m.Bounds.Left < origin.Bounds.Left+origin.Bounds.Width && m.Bounds.Left+m.Bounds.Width > origin.Bounds.Left));
        if (target is null) { Console.WriteLine("SKIP Live preset relay requires two adjacent monitors"); return; }
        var scenes=new List<Window>();
        using var session=new ScreenLensSession(nativePointer:false);
        async Task WaitFor(Func<bool> condition,string message)
        {
            var deadline=DateTime.UtcNow.AddSeconds(8);
            while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
            Require(condition(),message);
        }
        try
        {
            foreach (var (monitor,color) in new[] { (origin,Color.FromRgb(32,64,160)),(target,Color.FromRgb(40,140,70)) })
            {
                var scene=new Window { Background=new SolidColorBrush(color),Width=400,Height=300,
                    WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,Topmost=true,Left=-20000,Top=-20000 };
                scenes.Add(scene); scene.Show(); var b=monitor.Bounds;
                Require(SetWindowPos(new WindowInteropHelper(scene).Handle,new IntPtr(-1),b.Left,b.Top,b.Width,b.Height,0x10),"Preset relay scene did not cover its monitor");
            }
            await Task.Delay(120);
            // WPF can apply its suggested size after crossing DPI contexts on the first move.
            for(var i=0;i<scenes.Count;i++)
            {
                var bounds=i==0 ? origin.Bounds : target.Bounds;
                Require(SetWindowPos(new WindowInteropHelper(scenes[i]).Handle,new IntPtr(-1),bounds.Left,bounds.Top,bounds.Width,bounds.Height,0x10),"DPI-adjusted fixture did not fill its monitor");
            }
            await Task.Delay(120);
            foreach (var aspect in new[] { LensAspectRatio.Screen,LensAspectRatio.Square,LensAspectRatio.Wide })
            {
                session.SetMovement(true); session.SetBorderColor(0x123456);
                session.Start([origin,target],origin,640,384,zoom,.35,0,LensFrameSizePreset.Small,aspect);
                await WaitFor(() => session.IsVisible,"Preset capture did not start");
                var pointer=session.PointerForDiagnostics!; var handle=session.LensHandle;
                var state=session.MovementForDiagnostics!; var x=(int)state.X.OutputCenter; var y=(int)state.Y.OutputCenter;
                pointer.Process(0x201,x,y);
                foreach (var index in new[] { 1,0 })
                {
                    var sourceMonitor=index == 1 ? origin : target; var destination=index == 1 ? target : origin;
                    var a=sourceMonitor.Bounds; var b=destination.Bounds;
                    var horizontal=b.Left+b.Width <= a.Left || b.Left >= a.Left+a.Width;
                    var sign=horizontal ? (b.Left < a.Left ? -1 : 1) : (b.Top < a.Top ? -1 : 1);
                    state=session.MovementForDiagnostics!; var frame=state.Layout.Output;
                    // Keep the same grip through both handoffs. Moving the pointer to
                    // the frame center would move the frame again rather than regrip it.
                    if (horizontal) y+=(int)Math.Round((Math.Max(a.Top,b.Top)+Math.Min(a.Top+a.Height,b.Top+b.Height))/2d-state.Y.OutputCenter);
                    else x+=(int)Math.Round((Math.Max(a.Left,b.Left)+Math.Min(a.Left+a.Width,b.Left+b.Width))/2d-state.X.OutputCenter);
                    pointer.Process(0x200,x,y);
                    state=session.MovementForDiagnostics!;
                    var edge=horizontal ? (sign < 0 ? a.Left+frame.Width/2d : a.Left+a.Width-frame.Width/2d) :
                        (sign < 0 ? a.Top+frame.Height/2d : a.Top+a.Height-frame.Height/2d);
                    var distance=edge-(horizontal ? state.X.OutputCenter : state.Y.OutputCenter)+sign*2;
                    var step=(int)(sign < 0 ? Math.Floor(distance) : Math.Ceiling(distance));
                    if (horizontal) x+=step; else y+=step;
                    pointer.Process(0x200,x,y);
                    await WaitFor(() => session.MovementForDiagnostics?.Relay?.TargetMonitor == index,"Preset frame did not form a legal destination relay");
                    var relay=session.MovementForDiagnostics!.Relay!;
                    var expected=LensFrameSize.Resolve(b,640,384,LensFrameSizePreset.Small,aspect);
                    Require(relay.VirtualOutput.Width == expected.Width && relay.VirtualOutput.Height == expected.Height,"Virtual target used the origin's size");
                    var capture=session.CaptureForDiagnostics; var renderer=session.RendererForDiagnostics;
                    if (horizontal) x=sign < 0 ? b.Left+b.Width-1 : b.Left+1;
                    else y=sign < 0 ? b.Top+b.Height-1 : b.Top+1;
                    pointer.Process(0x200,x,y); await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Require(session.MovementForDiagnostics!.MonitorIndex != index && session.CaptureForDiagnostics == capture && session.RendererForDiagnostics == renderer,
                        "Preset mouse entry allocated a speculative renderer/capture");
                    // An odd pixel dimension has a half-pixel center; move to the first
                    // physical pixel beyond that axis rather than rounding back before it.
                    double CrossPixel(double axis) => sign < 0 ? Math.Floor(axis) : Math.Ceiling(axis);
                    if (horizontal) x=(int)CrossPixel(relay.VirtualOutput.Left+expected.Width/2d);
                    else y=(int)CrossPixel(relay.VirtualOutput.Top+expected.Height/2d);
                    pointer.Process(0x200,x,y);
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Require(session.MovementForDiagnostics?.MonitorIndex == index,"Preset relay did not consume its center axis");
                    await WaitFor(() => session.CaptureForDiagnostics != capture && session.CaptureForDiagnostics?.FramesRendered > 0,
                        "Preset handoff did not display the destination's live frame");
                    Require(session.LensHandle == handle && ReferenceEquals(pointer,session.PointerForDiagnostics) && session.ActiveBounds is { } active &&
                        active.Width == expected.Width && active.Height == expected.Height,"Automatic resizing recreated the native pointer/window or used wrong dimensions");
                    Require((frame.Width == expected.Width && frame.Height == expected.Height) == ReferenceEquals(renderer,session.RendererForDiagnostics),
                        "The renderer was recreated without a size change, or retained at the wrong size");
                    var pixels=session.CaptureForDiagnostics!.ReadOutput(); var center=((expected.Height/2)*expected.Width+expected.Width/2)*4;
                    var color=index == 1 ? Color.FromRgb(40,140,70) : Color.FromRgb(32,64,160);
                    Require(pixels.Length == expected.Width*expected.Height*4 && Math.Abs(pixels[center]-color.B)<=2 &&
                        Math.Abs(pixels[center+1]-color.G)<=2 && Math.Abs(pixels[center+2]-color.R)<=2 &&
                        pixels[0] == 0x56 && pixels[1] == 0x34 && pixels[2] == 0x12,
                        $"Resized live output mismatch at {zoom}x: {pixels.Length}/{expected.Width*expected.Height*4}; center BGRA {pixels[center]},{pixels[center+1]},{pixels[center+2]}; border {pixels[0]},{pixels[1]},{pixels[2]}");
                }
                pointer.Process(0x202,x,y); session.Stop(); Require(!session.HasCaptureResources,"Stopping a resized preset retained native resources");
            }
            Console.WriteLine($"PASS Live {zoom:0.00}x screen/1:1/16:9 presets resize on both relay directions, preserve rendered pixels/border color, and reuse the native window/pointer");
        }
        finally { session.Stop(); foreach (var scene in scenes) scene.Close(); }
    }
}
