using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DailyToolkit.Desktop.Gaming;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    // Measures input callback work on an actual capture/overlay, not end-to-end
    // display latency. The sampled monitor is covered by our own plain scene.
    private static async Task RunDragBenchmarkAsync(string output)
    {
        var monitor=LensNativeWindow.Monitors()[0]; var bounds=monitor.Bounds;
        var scene=new Window
        {
            Background=new SolidColorBrush(Color.FromRgb(32,64,160)),Width=400,Height=300,
            WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,
            ShowActivated=false,Topmost=true,Left=-20000,Top=-20000
        };
        using var session=new ScreenLensSession();
        try
        {
            scene.Show();
            Require(SetWindowPos(new WindowInteropHelper(scene).Handle,new IntPtr(-1),bounds.Left,bounds.Top,bounds.Width,bounds.Height,0x10),
                "Benchmark scene did not cover the sampled monitor");
            await Task.Delay(100);
            session.SetMovement(true); session.Start([monitor],monitor,640,384,2,0.35,0);
            var deadline=DateTime.UtcNow.AddSeconds(6);
            while (!session.IsVisible && DateTime.UtcNow < deadline) await Task.Delay(20);
            Require(session.IsVisible,"Benchmark capture did not start");
            var pointer=session.PointerForDiagnostics!; var frame=session.ActiveBounds!;
            var x=frame.Left+frame.Width/2; var y=frame.Top+frame.Height/2;
            Require(pointer.Process(0x201,x,y),"Benchmark drag did not begin");
            var timings=new List<double>(); var framesBefore=session.CaptureForDiagnostics!.FramesRendered;
            var elapsed=Stopwatch.StartNew();
            for (var i=0;i<1200;i++)
            {
                var dx=i%120 < 60 ? 3 : -3; x+=dx;
                var before=Stopwatch.GetTimestamp(); var handled=pointer.Process(0x200,x,y);
                timings.Add(Stopwatch.GetElapsedTime(before).TotalMilliseconds);
                Require(handled,"Benchmark drag was interrupted by real desktop input");
                if (i%24 == 23) await Task.Delay(1);
            }
            await Task.Delay(100); pointer.Process(0x202,x,y);
            timings.Sort();
            var result=new
            {
                Samples=timings.Count,CallbackMedianMs=timings[timings.Count/2],
                CallbackP95Ms=timings[(int)(timings.Count*0.95)],CallbackP99Ms=timings[(int)(timings.Count*0.99)],
                CallbackMaxMs=timings[^1],TotalMs=elapsed.Elapsed.TotalMilliseconds,
                Frames=session.CaptureForDiagnostics.FramesRendered-framesBefore,
                FinalFrameRestored=session.ActiveBounds == frame
            };
            Require(result.FinalFrameRestored,"Benchmark dropped accumulated movement");
            await File.WriteAllTextAsync(output,JsonSerializer.Serialize(result));
            Console.WriteLine(JsonSerializer.Serialize(result));
        }
        finally { session.Stop(); scene.Close(); }
    }
}
