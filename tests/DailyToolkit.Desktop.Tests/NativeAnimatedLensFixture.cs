using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using DailyToolkit.Desktop.Gaming;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DailyToolkit.Desktop.Tests;

// A dedicated native test window, animated away from the WPF dispatcher.
internal sealed class NativeAnimatedLensFixture : IDisposable
{
    private readonly HwndSource _window;
    private readonly LensGpuRenderer _renderer;
    private readonly ID3D11Texture2D _texture;
    private readonly CancellationTokenSource _stop=new();
    private readonly Task _worker;
    public NativeAnimatedLensFixture(CaptureMonitor monitor)
    {
        var b=monitor.Bounds;
        _window=new(new HwndSourceParameters("DailyToolkit generated capture benchmark")
        {PositionX=b.Left,PositionY=b.Top,Width=b.Width,Height=b.Height,WindowStyle=unchecked((int)0x90000000),ExtendedWindowStyle=0x08000080});
        SetWindowPos(_window.Handle,new IntPtr(-1),b.Left,b.Top,b.Width,b.Height,0x10);
        _renderer=new(_window.Handle,b.Width,b.Height);
        var pixels=new byte[128*96*4];LensTestScenes.Create()["GameTexture"].CopyPixels(pixels,128*4,0);
        var pin=GCHandle.Alloc(pixels,GCHandleType.Pinned);
        try { _texture=_renderer.Device.CreateTexture2D(new Texture2DDescription {Width=128,Height=96,MipLevels=1,ArraySize=1,
            Format=Format.B8G8R8A8_UNorm,SampleDescription=new(1,0),Usage=ResourceUsage.Default,BindFlags=BindFlags.ShaderResource},new SubresourceData(pin.AddrOfPinnedObject(),128*4)); }
        finally {pin.Free();}
        _worker=Task.Run(() =>
        {
            var watch=Stopwatch.StartNew();
            while (!_stop.IsCancellationRequested)
            {
                var offset=8*Math.Sin(watch.Elapsed.TotalSeconds*4);
                _renderer.Render(_texture,new(16+offset,12,96,72),0.35);
                Thread.Sleep(2);
            }
        });
    }
    public void Dispose() {_stop.Cancel();_worker.GetAwaiter().GetResult();_texture.Dispose();_renderer.Dispose();_window.Dispose();_stop.Dispose();}
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowPos(IntPtr window,IntPtr after,int x,int y,int width,int height,uint flags);
}
