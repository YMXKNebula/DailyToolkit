using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;
using Vortice.DXGI;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckLiveImageUpgradeAsync(string? output=null,int monitorIndex=0)
    {
        var monitor=LensNativeWindow.Monitors()[monitorIndex]; var bounds=monitor.Bounds;
        var scene=new Window {Background=Brushes.SteelBlue,Width=400,Height=300,WindowStyle=WindowStyle.None,
            ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,ShowActivated=false,Topmost=true,Left=-20000,Top=-20000};
        var timer=new DispatcherTimer(DispatcherPriority.Render){Interval=TimeSpan.FromMilliseconds(1)};
        var phase=0; timer.Tick+=(_,_)=>scene.Background=new SolidColorBrush(Color.FromRgb((byte)(32+phase++%120),64,160));
        using var session=new ScreenLensSession(nativePointer:false);
        try
        {
            scene.Show(); Require(SetWindowPos(new WindowInteropHelper(scene).Handle,new IntPtr(-1),bounds.Left,bounds.Top,bounds.Width,bounds.Height,0x10),"Own fixture did not cover capture monitor");
            timer.Start(); session.SetMovement(true); session.Start([monitor],monitor,640,384,16,0.35,0);
            async Task Visible() {var deadline=DateTime.UtcNow.AddSeconds(6);while(!session.IsVisible && DateTime.UtcNow<deadline)await Task.Delay(20);Require(session.IsVisible,"Capture did not start");}
            await Visible();
            var capture=session.CaptureForDiagnostics; var renderer=session.RendererForDiagnostics; var window=session.LensHandle; var pointer=session.PointerForDiagnostics!;
            foreach(var mode in new[]{LensImageMode.Pixel,LensImageMode.Clear,LensImageMode.HighQuality,LensImageMode.Performance,LensImageMode.AIEnhanced})
            {
                session.SetImageMode(mode); session.UpdatePicture(16,LensImageSettings.GetSharpening(mode)); await Task.Delay(30);
                Require(ReferenceEquals(capture,session.CaptureForDiagnostics) && ReferenceEquals(renderer,session.RendererForDiagnostics) && session.LensHandle == window && ReferenceEquals(pointer,session.PointerForDiagnostics),
                    "Image switching rebuilt the stable native/capture/input pipeline");
                Require(capture!.ImageModeForDiagnostics == LensImageSettings.NormalizeMode(mode),"Mode did not reach active capture renderer");
                Require(capture.SourceForDiagnostics.Sharpening == LensImageSettings.GetSharpening(mode),"Fixed mode strength did not reach the live renderer");
            }
            var requested=0; double zoom=3; var accumulator=new LensWheelAccumulator();
            session.UpdatePicture(zoom,1);
            session.ZoomRequested+=delta=>{requested++;var ticks=accumulator.Consume(delta);if(ticks!=0){zoom=LensZoom.Normalize(zoom+ticks*0.25);session.UpdatePicture(zoom,1);session.ShowZoomHud(zoom);}};
            var frame=session.ActiveBounds!; var x=frame.Left+frame.Width/2;var y=frame.Top+frame.Height/2;
            // Freeze unrelated asynchronous capture frames while checking callback deferral.
            capture!.Paused=true;await Task.Delay(30);var frames=capture.FramesRendered;
            Require(pointer.Process(0x20A,x,y,60) && pointer.Process(0x20A,x,y,60),"Wheel was not routed in movable mode");
            Require(requested==0 && zoom==3 && capture.FramesRendered==frames,"Wheel callback performed rendering or UI work before returning");
            capture.Paused=false;
            await Task.Delay(50);
            Require(zoom==3.25 && capture.HudForDiagnostics.Zoom==3.25 && capture.HudForDiagnostics.Opacity>0,"Quarter zoom HUD was not shown");
            zoom=16;session.UpdatePicture(zoom,1);pointer.Process(0x20A,x,y,120);pointer.Process(0x20A,x,y,-120);await Task.Delay(50);
            Require(zoom==15.75,"Deferred wheel lost reversal order at the upper boundary");
            zoom=3.25;session.UpdatePicture(zoom,1);
            session.SetMovement(false); pointer.Process(0x20A,x,y,120);await Task.Delay(50);
            Require(zoom==3.5 && capture.HudForDiagnostics.Zoom==3.5 && ReferenceEquals(capture,session.CaptureForDiagnostics),"Fixed mode HUD restarted capture or lost wheel zoom");
            await Task.Delay(900); Require(capture.HudForDiagnostics.Opacity==0,"Zoom HUD did not expire");
            session.SetWheelZoom(false); Require(!pointer.Process(0x20A,x,y,120),"Disabled wheel was intercepted");session.Stop();
            Require(!session.HasCaptureResources,"Stop retained image/HUD capture resources");
            Console.WriteLine("PASS Live modes at 16x reuse capture/window/input; deferred precision wheel and final HUD work in fixed/movable modes and expire");
            if(output is null) return;

            timer.Stop();scene.Hide();using var nativeScene=new NativeAnimatedLensFixture(monitor);
            var results=new List<object>(); using var gpuProbe=new GpuUsageProbe();
            foreach(var fps in new[]{60,120,144,240}) foreach(var mode in new[]{LensImageMode.Performance,LensImageMode.Clear,LensImageMode.HighQuality,LensImageMode.Pixel})
            {
                var strength=LensImageSettings.GetSharpening(mode);
                session.SetImageMode(mode);session.SetMovement(true);session.Start([monitor],monitor,640,384,1,strength,fps);await Visible();
                foreach(var factor in new[]{1d,2d,4d,8d,12d,16d})
                {
                    session.UpdatePicture(factor,strength);await Task.Delay(80);var active=session.CaptureForDiagnostics!;
                    var received=active.FramesReceived;var rendered=active.FramesRendered;var cpuBefore=Process.GetCurrentProcess().TotalProcessorTime;
                    gpuProbe.Begin();var watch=Stopwatch.StartNew();await Task.Delay(400);watch.Stop();var cpuAfter=Process.GetCurrentProcess().TotalProcessorTime;
                    var gpu=gpuProbe.Read();
                    ulong? videoMemory=null;
                    try {using var dxgi=session.RendererForDiagnostics!.Device.QueryInterface<IDXGIDevice>();using var adapter=dxgi.GetAdapter();using var adapter3=adapter.QueryInterface<IDXGIAdapter3>();videoMemory=adapter3.QueryVideoMemoryInfo(0,MemorySegmentGroup.Local).CurrentUsage;}catch(Exception){ }
                    results.Add(new{Mode=mode.ToString(),Zoom=factor,TargetFps=fps,DurationSeconds=watch.Elapsed.TotalSeconds,
                        CaptureFps=(active.FramesReceived-received)/watch.Elapsed.TotalSeconds,OutputFps=(active.FramesRendered-rendered)/watch.Elapsed.TotalSeconds,
                        CpuPercent=(cpuAfter-cpuBefore).TotalMilliseconds/watch.Elapsed.TotalMilliseconds/System.Environment.ProcessorCount*100,
                        ActualGpuUtilization=gpu is null ? (double?)null : gpu.Values.Max(),GpuEnginePercent=gpu,
                        ProcessVideoMemoryBytes=videoMemory,WindowsRemainVisible=session.IsVisible});
                }
                session.Stop();Console.WriteLine($"Measured {mode}: {fps} cap, 1/2/4/8/12/16x");
                await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new{Scene="Self-owned full-monitor native D3D animated texture window; includes fixture CPU and GPU cost",
                    SourceMonitor=monitor.Name,SourceBounds=bounds,
                    SourceRefreshConstrainedByWindowsAndDisplay=true,
                    GpuUtilizationDefinition="Maximum per-process GPU engine percent from PDH, includes own WPF fixture; raw engines supplied",
                    Results=results},new JsonSerializerOptions{WriteIndented=true}));
            }
        }
        finally {timer.Stop();session.Stop();scene.Close();}
    }
}
