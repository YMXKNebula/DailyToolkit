using System.Diagnostics;
using DailyUSE.Core.Gaming;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace DailyUSE.Desktop.Gaming;

internal sealed class LensCapture : IDisposable
{
    private readonly object _gate = new();
    private readonly LensGpuRenderer _renderer;
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session = null!;
    private readonly GraphicsCaptureItem _item;
    private readonly SourceArea _fixedSource;
    private readonly Func<SourceArea>? _source;
    private readonly double _sharpening;
    private readonly int _framesPerSecond;
    private readonly int _sourceWidth, _sourceHeight;
    private long _lastFrame;
    private bool _stopped, _failed;
    private volatile bool _paused;
    public bool Paused { get => _paused; set => _paused=value; }
    public event Action<string>? Failed;
    public event Action? FirstFrame;
    public long FramesRendered => _renderer.FramesRendered;

    public LensCapture(LensGpuRenderer renderer, GraphicsCaptureItem item, SourceArea source, double sharpening,
        int framesPerSecond = 60, Func<SourceArea>? movingSource = null)
    {
        _renderer = renderer; _item = item; _fixedSource = source; _source = movingSource;
        _sharpening = sharpening; _framesPerSecond = framesPerSecond;
        _sourceWidth=item.Size.Width; _sourceHeight=item.Size.Height;
        _device = CaptureInterop.Wrap(renderer.Device);
        try
        {
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            try
            {
                _session = _pool.CreateCaptureSession(item);
                _session.IsCursorCaptureEnabled = false;
                _pool.FrameArrived += OnFrame;
                _item.Closed += OnClosed;
            }
            catch { _session?.Dispose(); _pool.Dispose(); throw; }
        }
        catch { _device.Dispose(); throw; }
    }

    public void Start() => _session.StartCapture();

    private void OnFrame(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_stopped || _failed) return;
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null) return;
                if (Paused) return;
                if (frame.ContentSize.Width != _sourceWidth || frame.ContentSize.Height != _sourceHeight)
                    throw new InvalidOperationException("显示尺寸已变化，请重新开启放大框。");
                var now = Stopwatch.GetTimestamp();
                if (_lastFrame != 0 && Stopwatch.GetElapsedTime(_lastFrame,now).TotalSeconds < 1d/_framesPerSecond) return;
                _lastFrame = now;
                using var texture = CaptureInterop.Texture(frame.Surface);
                _renderer.Render(texture,_source?.Invoke() ?? _fixedSource,_sharpening);
                if (_renderer.FramesRendered == 1) FirstFrame?.Invoke();
            }
            catch (Exception)
            {
                _failed = true;
                Failed?.Invoke("画面读取已中断，请重新开启放大框。显示器、全屏模式或显卡状态可能发生了变化。");
            }
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
