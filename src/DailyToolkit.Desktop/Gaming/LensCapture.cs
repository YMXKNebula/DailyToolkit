using System.Diagnostics;
using DailyToolkit.Core.Gaming;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Foundation.Metadata;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace DailyToolkit.Desktop.Gaming;

internal sealed class LensCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly LensGpuRenderer _renderer;
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session = null!;
    private readonly GraphicsCaptureItem _item;
    private RenderSettings _settings;
    private readonly LensFramePacer _pacer;
    private readonly int _sourceWidth, _sourceHeight;
    private int _refreshRequested,_refreshWorker;
    private bool _stopped, _failed;
    private volatile bool _paused;
    private bool _firstFrameDelivered;
    private static readonly Lazy<Task<bool>> BorderlessAccess=new(RequestBorderlessAccessAsync);
    internal bool BorderlessAllowed { get; private set; }
    internal bool? BorderRequired => ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession","IsBorderRequired")
        ? _session.IsBorderRequired : null;
    public bool Paused { get => _paused; set => _paused=value; }
    public event Action<string>? Failed;
    public event Action? FirstFrame;
    public long FramesRendered => _renderer.FramesRendered;

    public LensCapture(LensGpuRenderer renderer, GraphicsCaptureItem item, SourceArea source, double sharpening,
        int framesPerSecond = 0,uint borderColor = LensBorderColor.DefaultRgb)
    {
        _renderer = renderer; _item = item;
        _settings=new(source,sharpening,borderColor);
        _pacer=new(framesPerSecond,Stopwatch.Frequency);
        _sourceWidth=item.Size.Width; _sourceHeight=item.Size.Height;
        _device = CaptureInterop.Wrap(renderer.Device);
        try
        {
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            try
            {
                _session = _pool.CreateCaptureSession(item);
                _session.IsCursorCaptureEnabled = false;
                if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession","MinUpdateInterval"))
                    _session.MinUpdateInterval=TimeSpan.Zero;
                _pool.FrameArrived += OnFrame;
                _item.Closed += OnClosed;
            }
            catch { _session?.Dispose(); _pool.Dispose(); throw; }
        }
        catch { _device.Dispose(); throw; }
    }

    private static async Task<bool> RequestBorderlessAccessAsync()
    {
        if (!ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession","IsBorderRequired")) return false;
        try { return await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless) == AppCapabilityAccessStatus.Allowed; }
        catch (Exception exception) { Trace.WriteLine(exception); return false; }
    }

    public async Task StartAsync()
    {
        var allowed=await BorderlessAccess.Value;
        lock (_gate)
        {
            if (_stopped) return;
            BorderlessAllowed=allowed;
            if (BorderRequired is not null) _session.IsBorderRequired=false;
            _session.StartCapture();
        }
    }

    private void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_stopped || _failed) return;
            try
            {
                var frame = sender.TryGetNextFrame();
                if (frame is null) return;
                // The pool holds at most two frames. Drop an older pending frame rather than
                // making input wait behind it; the bounded drain cannot starve rendering.
                try
                {
                    var latest=sender.TryGetNextFrame();
                    if (latest is not null) { frame.Dispose(); frame=latest; }
                    if (Paused) return;
                    if (frame.ContentSize.Width != _sourceWidth || frame.ContentSize.Height != _sourceHeight)
                        throw new InvalidOperationException("显示尺寸已变化，请重新开启放大框。");
                    var now = Stopwatch.GetTimestamp();
                    if (!_pacer.ShouldRender(now)) return;
                    using var texture = CaptureInterop.Texture(frame.Surface);
                    var settings=Volatile.Read(ref _settings);
                    _renderer.Render(texture,settings.Source,settings.Sharpening,borderColor:settings.BorderColor);
                    if (!_firstFrameDelivered) { _firstFrameDelivered=true; FirstFrame?.Invoke(); }
                }
                finally { frame.Dispose(); }
            }
            catch (Exception exception)
            {
                Trace.WriteLine(exception);
                _failed = true;
                Failed?.Invoke("画面读取已中断，请重新开启放大框。显示器、全屏模式或显卡状态可能发生了变化。");
            }
        }
    }

    public void UpdateSource(SourceArea source,double sharpening,uint? borderColor=null)
    {
        Volatile.Write(ref _settings,new(source,sharpening,borderColor ?? Volatile.Read(ref _settings).BorderColor));
        RequestRefresh();
    }

    private void RequestRefresh()
    {
        Interlocked.Exchange(ref _refreshRequested,1);
        if (Interlocked.CompareExchange(ref _refreshWorker,1,0) != 0) return;
        _=Task.Run(() =>
        {
            try
            {
                while (Interlocked.Exchange(ref _refreshRequested,0) != 0)
                {
                    lock (_gate)
                    {
                        if (_stopped || _failed || Paused) return;
                        var settings=Volatile.Read(ref _settings);
                        _renderer.RenderLast(settings.Source,settings.Sharpening,borderColor:settings.BorderColor);
                    }
                }
            }
            catch (Exception exception) { Trace.WriteLine(exception); Failed?.Invoke("画面更新已中断，请重新开启放大框。"); }
            finally
            {
                Interlocked.Exchange(ref _refreshWorker,0);
                if (Volatile.Read(ref _refreshRequested) != 0 && !_stopped)
                    RequestRefresh();
            }
        });
    }

    private sealed record RenderSettings(SourceArea Source,double Sharpening,uint BorderColor);

    internal (SourceArea Source,double Sharpening) SourceForDiagnostics
    {
        get
        {
            var settings=Volatile.Read(ref _settings);
            return (settings.Source,settings.Sharpening);
        }
    }

    private void OnClosed(GraphicsCaptureItem item, object args) => Failed?.Invoke("放大的画面已关闭。");
    public byte[] ReadOutput() { lock (_gate) return _renderer.ReadOutput(); }
    public void Dispose()
    {
        lock (_gate) { if (_stopped) return; _stopped = true; }
        _pool.FrameArrived -= OnFrame;
        _item.Closed -= OnClosed;
        // Closing outside the render lock allows pending callbacks to finish without a deadlock.
        _session.Dispose(); _pool.Dispose(); _device.Dispose();
    }
}
