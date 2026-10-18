using System.IO;

namespace DailyToolkit.Desktop.Runtime;

// The pipe exists only while the tool is enabled. Transitions are serialized so
// re-enabling never competes with an old server that is still closing its pipe.
internal sealed class FilePackRequestRuntime(Func<IAsyncDisposable> create, Action<Exception> failure) : IDisposable, IAsyncDisposable
{
    private IAsyncDisposable? _broker;
    private Task _pending = Task.CompletedTask;
    private bool _enabled, _disposed;
    internal bool IsActive => _broker is not null;
    internal Task Pending => _pending;
    internal void Configure(bool enabled)
    {
        if (_disposed) return;
        _enabled = enabled;
        _pending = ApplyAsync(_pending);
    }
    private async Task ApplyAsync(Task previous)
    {
        await previous;
        try
        {
            if (!_enabled || _disposed)
            {
                var broker = _broker; _broker = null;
                if (broker is not null) await broker.DisposeAsync();
            }
            if (_enabled && !_disposed && _broker is null) _broker = create();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        { failure(error); }
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _enabled = false;
        if (_broker is IDisposable disposable) disposable.Dispose();
        _pending = ApplyAsync(_pending);
    }
    public async ValueTask DisposeAsync() { Dispose(); await _pending; }
}
