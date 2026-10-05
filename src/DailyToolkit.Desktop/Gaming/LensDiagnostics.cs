using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DailyToolkit.Core.Gaming;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DailyToolkit.Desktop.Gaming;

internal static class LensDiagnostics
{
    public static unsafe void CheckShader()
    {
        using var window = new LensNativeWindow(new(-20000,-20000,256,160));
        using var renderer = new LensGpuRenderer(window.Handle,256,160,software: true);
        var pixels = new byte[32*20*4];
        for (var y=0;y<20;y++) for (var x=0;x<32;x++)
        {
            var at = (y*32+x)*4;
            pixels[at] = 100; pixels[at+1] = 140; pixels[at+2] = (byte)(x < 16 ? 64 : 192); pixels[at+3] = 255;
        }
        fixed (byte* pointer = pixels)
        {
            using var texture = renderer.Device.CreateTexture2D(new Texture2DDescription
            {
                Width=32,Height=20,MipLevels=1,ArraySize=1,Format=Format.B8G8R8A8_UNorm,
                SampleDescription=new(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.ShaderResource
            },new SubresourceData(new IntPtr(pointer),32*4));
            renderer.Render(texture,new(0,0,32,20),0,present:false);
            renderer.Render(texture,new(3.2,2,25.6,16),0,present:false);
            var soft=renderer.ReadOutput();
            renderer.Render(texture,new(3.2,2,25.6,16),1,present:false);
            var sharp=renderer.ReadOutput();
            double EdgeEnergy(byte[] data)
            {
                double energy=0;
                for (var x=60;x<195;x++)
                {
                    var diff=data[(80*256+x)*4+2]-data[(80*256+x-1)*4+2];
                    energy+=diff*diff;
                }
                return energy;
            }
            if (EdgeEnergy(sharp) <= EdgeEnergy(soft)) throw new InvalidOperationException("Sharpening did not increase edge definition at 10x.");
            for (var x=4;x<252;x++)
            {
                var at=(80*256+x)*4;
                if (sharp[at+2] < 63 || sharp[at+2]>193 || Math.Abs(sharp[at]-100)>1 || Math.Abs(sharp[at+1]-140)>1)
                    throw new InvalidOperationException("High-zoom sharpening introduced halos or changed flat colors.");
            }
            renderer.Render(texture,new(0,0,32,20),0,present:false);
        }
        var output = renderer.ReadOutput();
        var atCenter = (80*256+127)*4;
        if (Math.Abs(output[atCenter]-100)>1 || Math.Abs(output[atCenter+1]-140)>1 ||
            output[atCenter+2] <=64 || output[atCenter+2]>=192) throw new InvalidOperationException("GPU reconstruction did not preserve colors and interpolate the edge.");
        if (output[1] < 170) throw new InvalidOperationException("The magnifier border was not rendered.");
    }

    public static async Task RunCaptureAsync(string outputPath)
    {
        CheckShader();
        var monitor = LensNativeWindow.Monitors().First();
        var canvas = new System.Windows.Controls.Grid { Background = new SolidColorBrush(Color.FromRgb(32,64,160)) };
        var scene = new Window
        {
            Title="DailyToolkit 放大测试",Width=440,Height=260,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,
            ShowInTaskbar=false,ShowActivated=false,Topmost=true,WindowStartupLocation=WindowStartupLocation.Manual,
            Left=40,Top=40,Content=canvas
        };
        LensNativeWindow? lens=null;
        LensGpuRenderer? renderer=null;
        LensCapture? capture=null;
        try
        {
            var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            scene.ContentRendered += (_,_) => rendered.TrySetResult();
            scene.Show();
            await rendered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            GetWindowRect(new WindowInteropHelper(scene).Handle,out var bounds);
            var centerX=(bounds.Left+bounds.Right)/2;
            var centerY=(bounds.Top+bounds.Bottom)/2;
            var layout = LensLayout.Calculate(monitor.Bounds,320,192,2,centerX,centerY);
            lens = new(layout.Output);
            renderer = new(lens.Handle,320,192);
            capture = new(renderer,CaptureInterop.ForMonitor(monitor.Handle),layout.Source,0.35);
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture.FirstFrame += () => first.TrySetResult();
            capture.Failed += reason => first.TrySetException(new InvalidOperationException(reason));
            capture.Start();
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var firstOutput = capture.ReadOutput();
            lens.Show();
            if (WindowFromPoint(new Point { X=centerX,Y=centerY }) != new WindowInteropHelper(scene).Handle)
                throw new InvalidOperationException("The lens intercepted mouse input instead of forwarding it to the underlying window.");
            // Change the underlying scene while the lens covers it. A captured lens would feed back or go black.
            for (var i=0;i<4;i++)
            {
                var before=capture.FramesRendered;
                canvas.Background=new SolidColorBrush(Color.FromRgb((byte)(32+i*10),64,160));
                var deadline=DateTime.UtcNow.AddSeconds(3);
                var index=(30*320+30)*4;
                byte[] pixels;
                bool matches;
                do
                {
                    await Task.Delay(30);
                    pixels=capture.ReadOutput();
                    matches=capture.FramesRendered>before && Math.Abs(pixels[index]-160)<=2 &&
                        Math.Abs(pixels[index+1]-64)<=2 && Math.Abs(pixels[index+2]-(32+i*10))<=2;
                } while(!matches && DateTime.UtcNow<deadline);
                if(!matches)
                    throw new InvalidOperationException($"Capture did not show the underlying scene at step {i}: " +
                        $"RGB {pixels[index+2]},{pixels[index+1]},{pixels[index]}, frames {before}->{capture.FramesRendered}; " +
                        $"before lens RGB {firstOutput[index+2]},{firstOutput[index+1]},{firstOutput[index]}; " +
                        $"center RGB {pixels[(96*320+160)*4+2]},{pixels[(96*320+160)*4+1]},{pixels[(96*320+160)*4]}; " +
                        $"scene {bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}, layout {JsonSerializer.Serialize(layout)}.");
            }
            var output = capture.ReadOutput();
            var bitmap=BitmapSource.Create(320,192,96,96,PixelFormats.Bgra32,null,output,320*4);
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create(outputPath)) encoder.Save(file);
            await File.WriteAllTextAsync(Path.ChangeExtension(outputPath,".json"),JsonSerializer.Serialize(new
            {
                Shader="Catmull-Rom + bounded sharpening",ShaderCheck=true,MonitorCapture=true,
                OverlayExcluded=true,MousePassThrough=true,Frames=capture.FramesRendered,OutputWidth=320,OutputHeight=192
            }));
        }
        finally { lens?.Hide(); capture?.Dispose(); renderer?.Dispose(); lens?.Dispose(); scene.Close(); }
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left,Top,Right,Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Point { public int X,Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window,out Rect bounds);
}
