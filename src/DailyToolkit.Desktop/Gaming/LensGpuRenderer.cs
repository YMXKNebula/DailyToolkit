using System.IO;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using DailyToolkit.Core.Gaming;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Buffer = Vortice.Direct3D11.ID3D11Buffer;

namespace DailyToolkit.Desktop.Gaming;

internal sealed class LensGpuRenderer : IDisposable
{
    private sealed record ShaderCode(byte[] Vertex,byte[] Pixel);
    private sealed record QualityShaderCode(byte[] Scale,byte[] Resolve);
    private static readonly Lazy<ShaderCode> CompiledShader=new(() => CompileShader(null));
    private static readonly Lazy<QualityShaderCode> CompiledQualityShader=new(() => CompileQualityShader(ReadShader()));
    internal static Task WarmShaderAsync() => Task.Run(() => { try { _=CompiledShader.Value; _=CompiledQualityShader.Value; } catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); } });
    private static string ReadShader()
    {
        using var resource=typeof(LensGpuRenderer).Assembly.GetManifestResourceStream("DailyToolkit.Desktop.Gaming.Lens.hlsl")!;
        using var reader=new StreamReader(resource); return reader.ReadToEnd();
    }
    private static QualityShaderCode CompileQualityShader(string shader) => new(
        Compiler.Compile(shader,"PSQualityScale","Lens.hlsl","ps_5_0",ShaderFlags.OptimizationLevel3).Span.ToArray(),
        Compiler.Compile(shader,"PSQualityResolve","Lens.hlsl","ps_5_0",ShaderFlags.OptimizationLevel3).Span.ToArray());
    private static ShaderCode CompileShader(string? shaderSource)
    {
        using var resource=typeof(LensGpuRenderer).Assembly.GetManifestResourceStream("DailyToolkit.Desktop.Gaming.Lens.hlsl")!;
        using var reader=new StreamReader(resource);
        var shader=shaderSource ?? reader.ReadToEnd();
        return new(Compiler.Compile(shader,"VS","Lens.hlsl","vs_5_0",ShaderFlags.OptimizationLevel3).Span.ToArray(),
            Compiler.Compile(shader,"PS","Lens.hlsl","ps_5_0",ShaderFlags.OptimizationLevel3).Span.ToArray());
    }
    public ID3D11Device Device { get; private set; } = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGISwapChain1? _swapChain;
    private ID3D11Texture2D? _backBuffer, _output, _source;
    private ID3D11RenderTargetView? _target;
    private ID3D11ShaderResourceView? _sourceView;
    private ID3D11VertexShader? _vertex;
    private ID3D11PixelShader? _pixel;
    private ID3D11PixelShader? _qualityScale,_qualityResolve;
    private ID3D11Texture2D? _qualityOutput;
    private ID3D11RenderTargetView? _qualityTarget;
    private ID3D11ShaderResourceView? _qualityView;
    private readonly string? _customShader;
    private readonly bool _supportsQuality;
    private Buffer? _parameters;
    private int _width, _height;
    public long FramesRendered { get; private set; }
    internal bool HasQualityIntermediate => _qualityOutput is not null;
    internal ID3D11Texture2D? QualityTargetForDiagnostics => _qualityOutput;
    internal int LastDrawPassCount { get; private set; }

    public LensGpuRenderer(IntPtr window, int width, int height, bool software = false, string? shaderSource = null)
    {
        _width = width; _height = height;
        _customShader=shaderSource;
        _supportsQuality=shaderSource is null || shaderSource.Contains("PSQualityScale(",StringComparison.Ordinal);
        try
        {
            Vortice.Direct3D11.D3D11.D3D11CreateDevice(IntPtr.Zero, software ? DriverType.Warp : DriverType.Hardware,
                DeviceCreationFlags.BgraSupport, new[] { FeatureLevel.Level_11_0 },
                out var device, out _, out var context).CheckError();
            Device = device; _context = context;
            var shader=shaderSource is null ? CompiledShader.Value : CompileShader(shaderSource);
            _vertex=Device.CreateVertexShader(shader.Vertex);
            _pixel=Device.CreatePixelShader(shader.Pixel);
            _parameters = Device.CreateBuffer(new BufferDescription(80, BindFlags.ConstantBuffer, ResourceUsage.Default));
            if (window != IntPtr.Zero)
            {
                using var dxgiDevice = Device.QueryInterface<IDXGIDevice1>();
                dxgiDevice.SetMaximumFrameLatency(1).CheckError();
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

    private void EnsureQualityTarget()
    {
        if (_qualityScale is null)
        {
            var shader=_customShader is null ? CompiledQualityShader.Value : CompileQualityShader(_customShader);
            _qualityScale=Device.CreatePixelShader(shader.Scale);
            _qualityResolve=Device.CreatePixelShader(shader.Resolve);
        }
        if (_qualityOutput is not null) return;
        _qualityOutput=Device.CreateTexture2D(new Texture2DDescription {
            Width=(uint)_width,Height=(uint)_height,MipLevels=1,ArraySize=1,
            Format=Format.R16G16B16A16_Float,SampleDescription=new(1,0),Usage=ResourceUsage.Default,
            BindFlags=BindFlags.RenderTarget|BindFlags.ShaderResource });
        _qualityTarget=Device.CreateRenderTargetView(_qualityOutput);
        _qualityView=Device.CreateShaderResourceView(_qualityOutput);
    }
    private void ReleaseQualityTarget()
    {
        _qualityView?.Dispose(); _qualityView=null;
        _qualityTarget?.Dispose(); _qualityTarget=null;
        _qualityOutput?.Dispose(); _qualityOutput=null;
    }

    public void ResizePreview(int width, int height)
    {
        if (_swapChain is not null) throw new InvalidOperationException("Only offscreen previews can be resized.");
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (_width == width && _height == height) return;
        _context.ClearState();
        ReleaseQualityTarget();
        _target?.Dispose(); _target=null; _output?.Dispose(); _output=null;
        _width=width; _height=height;
        CreateOutput();
    }

    public void Render(ID3D11Texture2D texture, SourceArea source, double sharpening, bool present = true,
        uint borderColor = LensBorderColor.DefaultRgb, LensHudFrame hud = default,LensImageMode imageMode=LensImageMode.Performance)
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
        RenderLast(source,sharpening,present,borderColor,hud,imageMode);
    }

    public void RenderLast(SourceArea source, double sharpening, bool present = true,
        uint borderColor = LensBorderColor.DefaultRgb, LensHudFrame hud = default,LensImageMode imageMode=LensImageMode.Performance)
    {
        if (_source is null) return;
        imageMode=LensImageSettings.NormalizeMode(imageMode);
        var description=_source.Description;
        var parameters = new Parameters
        {
            Source = new((float)source.Left, (float)source.Top, (float)source.Width, (float)source.Height),
            Dimensions = new(description.Width, description.Height, _width, _height),
            Options = new((float)Math.Clamp(sharpening,0,1),2,(float)LensImageSettings.NormalizeMode(imageMode),(float)(_width/source.Width)),
            Hud = new((float)LensZoom.Normalize(hud.Zoom),Math.Clamp(hud.Opacity,0,1),0,0),
            // Keep the original default's shader values; custom colors use exact RGB bytes.
            BorderColor = borderColor == LensBorderColor.DefaultRgb ? new(0.20f,0.70f,0.54f,1) :
                new((borderColor >> 16 & 255)/255f,(borderColor >> 8 & 255)/255f,(borderColor & 255)/255f,1)
        };
        _context.UpdateSubresource(in parameters, _parameters!);
        _context.RSSetViewport(new Viewport(0,0,_width,_height));
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertex);
        _context.PSSetConstantBuffer(0,_parameters);
        _context.PSSetShaderResource(0,_sourceView!);
        if (imageMode == LensImageMode.HighQuality && _supportsQuality)
        {
            EnsureQualityTarget();
            _context.OMSetRenderTargets(_qualityTarget!);
            _context.PSSetShader(_qualityScale);
            _context.Draw(3,0);
            _context.OMSetRenderTargets(_target!);
            _context.PSSetShaderResource(1,_qualityView!);
            _context.PSSetShader(_qualityResolve);
            _context.Draw(3,0);
            _context.PSSetShaderResource(1,null!);
            LastDrawPassCount=2;
        }
        else
        {
            if (_qualityOutput is not null) ReleaseQualityTarget();
            _context.OMSetRenderTargets(_target!);
            _context.PSSetShader(_pixel);
            _context.Draw(3,0);
            LastDrawPassCount=1;
        }
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

    // Developer benchmark only: timestamps measure GPU completion without image readback.
    internal double MeasureGpuRender(SourceArea area,double sharpening,int iterations,LensImageMode imageMode=LensImageMode.Performance)
    {
        using var disjoint=Device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint));
        using var start=Device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        using var end=Device.CreateQuery(new QueryDescription(QueryType.Timestamp));
        _context.Begin(disjoint); _context.End(start);
        for (var i=0;i<iterations;i++) RenderLast(area,sharpening,present:false,imageMode:imageMode);
        _context.End(end); _context.End(disjoint); _context.Flush();
        var deadline=Stopwatch.StartNew();
        while (!_context.IsDataAvailable(disjoint))
        {
            if (deadline.Elapsed.TotalSeconds>5) throw new TimeoutException("GPU timing did not complete.");
            Thread.Yield();
        }
        var frequency=_context.GetData<QueryDataTimestampDisjoint>(disjoint,AsyncGetDataFlags.None);
        if (frequency.Disjoint) throw new InvalidOperationException("GPU clock changed during the benchmark.");
        var first=_context.GetData<ulong>(start,AsyncGetDataFlags.None);
        var last=_context.GetData<ulong>(end,AsyncGetDataFlags.None);
        return (last-first)*1000d/frequency.Frequency/iterations;
    }

    public void Dispose()
    {
        _context?.ClearState(); _context?.Flush();
        ReleaseQualityTarget(); _qualityScale?.Dispose(); _qualityResolve?.Dispose();
        _sourceView?.Dispose(); _source?.Dispose(); _target?.Dispose(); _output?.Dispose(); _backBuffer?.Dispose();
        _parameters?.Dispose(); _pixel?.Dispose(); _vertex?.Dispose(); _swapChain?.Dispose(); _context?.Dispose(); Device?.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Parameters { public Vector4 Source, Dimensions, Options, BorderColor, Hud; }
}
