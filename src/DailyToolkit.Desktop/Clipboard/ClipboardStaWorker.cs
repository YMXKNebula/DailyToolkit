using System.Windows.Threading;

namespace DailyToolkit.Desktop.ClipRecall;

// Created lazily only when clipboard work is needed. The STA message loop sleeps
// when idle and also pumps messages required by delayed-rendering clipboard owners.
internal sealed class ClipboardStaWorker : IAsyncDisposable
{
    private readonly TaskCompletionSource<Dispatcher> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private bool _disposed;
    public ClipboardStaWorker()
    {
        _thread = new(() => { var dispatcher = Dispatcher.CurrentDispatcher; _ready.SetResult(dispatcher); Dispatcher.Run(); })
        { IsBackground = true, Name = "DailyToolkit ClipRecall STA" };
        _thread.SetApartmentState(ApartmentState.STA); _thread.Start();
    }
    public async Task<T> InvokeAsync<T>(Func<T> operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var dispatcher = await _ready.Task.ConfigureAwait(false);
        return await dispatcher.InvokeAsync(operation, DispatcherPriority.Background).Task.ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        var dispatcher = await _ready.Task.ConfigureAwait(false);
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        await Task.Run(() => _thread.Join()).ConfigureAwait(false);
    }
}
