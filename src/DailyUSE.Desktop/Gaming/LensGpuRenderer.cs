using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyUSE.Core.Gaming;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Buffer = Vortice.Direct3D11.ID3D11Buffer;

namespace DailyUSE.Desktop.Gaming;

internal sealed class LensGpuRenderer : IDisposable
{
    public ID3D11Device Device { get; private set; } = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _backBuffer, _output, _source;
    private ID3D11RenderTargetView? _target;
    private ID3D11ShaderResourceView? _sourceView;
    private ID3D11VertexShader? _vertex;
    private ID3D11PixelShader? _pixel;
    private Buffer? _parameters;
    private int _width, _height;
    public long FramesRendered { get; private set; }

    public LensGpuRenderer(IntPtr window, int width, int height, bool software = false)
    {
        _width = width; _height = height;
        try
        {
            Vortice.Direct3D11.D3D11.D3D11CreateDevice(IntPtr.Zero, software ? DriverType.Warp : DriverType.Hardware,
                DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 },
                out var device, out _, out var context).CheckError();
            Device = device; _context = context;
            using var resource = typeof(LensGpuRenderer).Assembly.GetManifestResourceStream("DailyUSE.Desktop.Gaming.Lens.hlsl")!;
            using var reader = new StreamReader(resource);
            var shader = reader.ReadToEnd();
            _vertex = Device.CreateVertexShader(Compiler.Compile(shader, "VS", "Lens.hlsl", "vs_5_0", ShaderFlags.OptimizationLevel3).Span);
            _pixel = Device.CreatePixelShader(Compiler.Compile(shader, "PS", "Lens.hlsl", "ps_5_0", ShaderFlags.OptimizationLevel3).Span);
            _parameters = Device.CreateBuffer(new BufferDescription(48, BindFlags.ConstantBuffer, ResourceUsage.Default));
            if (window != IntPtr.Zero)
            {
                using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
                using var adapter = dxgiDevice.GetAdapter();
                using var factory = adapter.GetParent<IDXGIFactory2>();
                _swapChain = factory.CreateSwapChainForHwnd(Device, window, new SwapChainDescription1
                {
                    Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new(1,0), BufferUsage = Usage.RenderTargetOutput, BufferCount = 2,
                    SwapEffect = SwapEffect.Discard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore
                });
                factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter);
                _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            }
            CreateOutput();
        }
        catch { Dispose(); throw; }
    }

    private void CreateOutput()
    {
        _output = Device.CreateTexture2D(new Texture2DDescription
        {
            Width=(uint)_width, Height=(uint)_height, MipLevels=1, ArraySize=1,
            Format=Format.B8G8R8A8_UNorm, SampleDescription=new(1,0),
            Usage=ResourceUsage.Default, BindFlags=BindFlags.RenderTarget
        });
        _target = Device.CreateRenderTargetView(_output);
    }

    public void ResizePreview(int width, int height)
    {
        if (_swapChain is not null) throw new InvalidOperationException("Only offscreen previews can be resized.");
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (_width == width && _height == height) return;
        _context.ClearState();
        _target?.Dispose(); _target=null; _output?.Dispose(); _output=null;
        _width=width; _height=height;
        CreateOutput();
    }

    public void Render(ID3D11Texture2D texture, SourceArea source, double sharpening, bool present = true,
        bool verticalGuide = false, bool horizontalGuide = false)
    {
        var description = texture.Description;
        if (_source is null || _source.Description.Width != description.Width || _source.Description.Height != description.Height)
        {
            _sourceView?.Dispose(); _source?.Dispose();
            description.BindFlags = BindFlags.ShaderResource;
            description.CPUAccessFlags = CpuAccessFlags.None;
            description.Usage = ResourceUsage.Default;
            description.MiscFlags = ResourceOptionFlags.None;
            _source = Device.CreateTexture2D(description);
            _sourceView = Device.CreateShaderResourceView(_source);
        }
        _context.CopyResource(_source, texture);
        RenderLast(source,sharpening,present,verticalGuide,horizontalGuide);
    }

    public void RenderLast(SourceArea source, double sharpening, bool present = true,
        bool verticalGuide = false, bool horizontalGuide = false)
    {
        if (_source is null) return;
        var description=_source.Description;
        var parameters = new Parameters
        {
            Source = new((float)source.Left, (float)source.Top, (float)source.Width, (float)source.Height),
            Dimensions = new(description.Width, description.Height, _width, _height),
            Options = new((float)Math.Clamp(sharpening,0,1),2,verticalGuide ? 1 : 0,horizontalGuide ? 1 : 0)
        };
        _context.UpdateSubresource(in parameters, _parameters!);
        _context.OMSetRenderTargets(_target!);
        _context.RSSetViewport(new Viewport(0,0,_width,_height));
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertex);
        _context.PSSetShader(_pixel);
        _context.PSSetConstantBuffer(0,_parameters);
        _context.PSSetShaderResource(0,_sourceView!);
        _context.Draw(3,0);
        _context.PSSetShaderResource(0,null!);
        if (present)
        {
            _context.CopyResource(_backBuffer!,_output!);
            _swapChain!.Present(0,PresentFlags.None).CheckError();
        }
        FramesRendered++;
    }

    public byte[] ReadOutput()
    {
        var description = _output!.Description;
        description.BindFlags = BindFlags.None;
        description.Usage = ResourceUsage.Staging;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = Device.CreateTexture2D(description);
        _context.CopyResource(staging,_output);
        var mapped = _context.Map(staging,0,MapMode.Read,Vortice.Direct3D11.MapFlags.None);
        try
        {
            var pixels = new byte[_width*_height*4];
            for (var row=0;row<_height;row++) Marshal.Copy(mapped.DataPointer + (int)mapped.RowPitch*row, pixels, row*_width*4, _width*4);
            return pixels;
        }
        finally { _context.Unmap(staging,0); }
    }

    public void Dispose()
    {
        _context?.ClearState(); _context?.Flush();
        _sourceView?.Dispose(); _source?.Dispose(); _target?.Dispose(); _output?.Dispose(); _backBuffer?.Dispose();
        _parameters?.Dispose(); _pixel?.Dispose(); _vertex?.Dispose(); _swapChain?.Dispose(); _context?.Dispose(); Device?.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Parameters { public Vector4 Source, Dimensions, Options; }
}
