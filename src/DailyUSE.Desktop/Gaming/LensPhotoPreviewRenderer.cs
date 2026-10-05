using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DailyUSE.Core.Gaming;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DailyUSE.Desktop.Gaming;

internal sealed record LensPreviewSettings(int ScreenWidth, int ScreenHeight, int Width, int Height,
    double Zoom, double Sharpening, int FrameRate, double PointerX=0.5, double PointerY=0.5);

internal sealed record LensPreviewFrame(BitmapSource Photo, BitmapSource Magnified, LensLayout Layout,
    LensPreviewSettings Settings);

// A local photo uses the same shader and layout as the live lens. No screen capture is opened here.
internal sealed class LensPhotoPreviewRenderer(bool software = false) : IDisposable
{
    private static readonly Lazy<BitmapSource> Photo = new(LoadPhoto);
    private readonly object _gate = new();
    private LensGpuRenderer? _renderer;
    private ID3D11Texture2D? _texture;
    private bool _disposed;

    private static BitmapSource LoadPhoto()
    {
        using var stream = typeof(LensPhotoPreviewRenderer).Assembly.GetManifestResourceStream("DailyUSE.Desktop.Assets.lens-preview.jpg")
            ?? throw new IOException("The lens preview photo is missing.");
        var source = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var bitmap = new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
        bitmap.Freeze();
        return bitmap;
    }

    public unsafe LensPreviewFrame Render(LensPreviewSettings settings)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed,this);
            var photo = Photo.Value;
            var monitor = new PixelBounds(0,0,settings.ScreenWidth,settings.ScreenHeight);
            var centerX = (int)Math.Round(settings.PointerX*monitor.Width);
            var centerY = (int)Math.Round(settings.PointerY*monitor.Height);
            var layout = LensLayout.Calculate(monitor,settings.Width,settings.Height,settings.Zoom,centerX,centerY);
            if (_renderer is null)
            {
                try { _renderer=new(IntPtr.Zero,layout.Output.Width,layout.Output.Height,software); }
                catch when (!software) { _renderer=new(IntPtr.Zero,layout.Output.Width,layout.Output.Height,software:true); }
                var pixels = new byte[photo.PixelWidth*photo.PixelHeight*4];
                photo.CopyPixels(pixels,photo.PixelWidth*4,0);
                fixed (byte* pointer = pixels)
                {
                    _texture = _renderer.Device.CreateTexture2D(new Texture2DDescription
                    {
                        Width=(uint)photo.PixelWidth, Height=(uint)photo.PixelHeight, MipLevels=1, ArraySize=1,
                        Format=Format.B8G8R8A8_UNorm, SampleDescription=new(1,0),
                        Usage=ResourceUsage.Default, BindFlags=BindFlags.ShaderResource
                    },new SubresourceData(new IntPtr(pointer),(uint)(photo.PixelWidth*4)));
                }
            }
            _renderer.ResizePreview(layout.Output.Width,layout.Output.Height);
            // Match the background image's UniformToFill crop for the selected monitor's aspect ratio.
            var scale = Math.Max(monitor.Width/(double)photo.PixelWidth,monitor.Height/(double)photo.PixelHeight);
            var offsetX = (monitor.Width-photo.PixelWidth*scale)/2;
            var offsetY = (monitor.Height-photo.PixelHeight*scale)/2;
            var source = new SourceArea((layout.Source.Left-offsetX)/scale,(layout.Source.Top-offsetY)/scale,
                layout.Source.Width/scale,layout.Source.Height/scale);
            _renderer.Render(_texture!,source,settings.Sharpening,present:false);
            var output = BitmapSource.Create(layout.Output.Width,layout.Output.Height,96,96,
                PixelFormats.Bgra32,null,_renderer.ReadOutput(),layout.Output.Width*4);
            output.Freeze();
            return new(photo,output,layout,settings);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed=true;
            _texture?.Dispose(); _renderer?.Dispose();
            _texture=null; _renderer=null;
        }
    }
}
