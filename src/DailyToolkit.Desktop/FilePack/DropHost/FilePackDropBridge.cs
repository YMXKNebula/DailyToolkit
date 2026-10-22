using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace DailyToolkit.Desktop.FilePack.DropHost;

// One bridge belongs to one visible tool window. It never owns the draft or
// performs file operations; it hands validated paths to the original model.
internal sealed class FilePackDropBridge : IAsyncDisposable
{
    private readonly string _executable;
    private readonly Func<IReadOnlyList<string>, Task> _receive;
    private readonly Action _failure;
    private readonly Func<string, string, int, Process> _launch;
    private readonly Func<uint, bool> _peer;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<FilePackDropMessage> _commands = Channel.CreateBounded<FilePackDropMessage>(new BoundedChannelOptions(8)
        { SingleReader = true, SingleWriter = false });
    private readonly Dictionary<int, TaskCompletionSource> _drains = [];
    private NamedPipeServerStream? _pipe;
    private Process? _process;
    private Task _reader = Task.CompletedTask, _writer = Task.CompletedTask;
    private Task _starting = Task.CompletedTask;
    private Task? _disposing;
    private readonly object _disposeGate = new();
    private FilePackDropMessage _palette;
    private int _nextDrain, _disposed, _expectedProcessId;
    internal IntPtr Handle { get; private set; }
    internal int ExpectedProcessId => Volatile.Read(ref _expectedProcessId);
    internal bool IsHostAlive
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            var process = Volatile.Read(ref _process);
            try { return process is not null && process.Id == ExpectedProcessId && !process.HasExited; }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { return false; }
        }
    }
    internal bool IsReady => Handle != IntPtr.Zero && Volatile.Read(ref _disposed) == 0 && !_reader.IsCompleted;
    internal string PipeName { get; } = FilePackDropProcess.PipePrefix + Guid.NewGuid().ToString("N");

    internal FilePackDropBridge(Func<IReadOnlyList<string>, Task> receive, Action failure, FilePackDropMessage palette,
        Func<string, string, int, Process>? launch = null, Func<uint, bool>? validatePeer = null, string? executable = null)
    {
        _receive = receive; _failure = failure; _palette = palette;
        _executable = executable ?? System.Environment.ProcessPath ?? throw new InvalidOperationException("当前程序路径不可用。");
        _launch = launch ?? FilePackDropProcess.Start;
        using var current = Process.GetCurrentProcess(); var session = current.SessionId;
        _peer = validatePeer ?? (id => FilePackDropProcess.IsTrustedPeer(id, _executable, session, standard: true));
    }

    internal Task StartAsync() => _starting = StartCoreAsync();

    private async Task StartCoreAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            var pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 0, 0, FilePackRequestBroker.CreatePipeSecurity());
            _pipe = pipe;
            // The returned Process is retained even if the window closes during launch.
            _process = await Task.Run(() => _launch(_executable, PipeName, System.Environment.ProcessId)).ConfigureAwait(false);
            Volatile.Write(ref _expectedProcessId, _process.Id);
            _stop.Token.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await pipe.WaitForConnectionAsync(timeout.Token).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) || processId != _process.Id || !_peer(processId))
                throw new UnauthorizedAccessException("拖放接收端身份不匹配。");
            await FilePackDropWire.WriteAsync(pipe, FilePackDropWire.Encode(Volatile.Read(ref _palette)), timeout.Token).ConfigureAwait(false);
            var ready = await FilePackDropWire.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            var handle = new IntPtr(ready.Window);
            if (ready.Kind != "ready" || handle == IntPtr.Zero || GetWindowThreadProcessId(handle, out var owner) == 0 || owner != processId)
                throw new InvalidDataException("拖放接收端窗口无效。");
            Handle = handle;
            _reader = ReadAsync(pipe); _writer = WriteAsync(pipe);
        }
        catch
        {
            _stop.Cancel(); _pipe?.Dispose();
            throw;
        }
    }

    internal void UpdatePalette(FilePackDropMessage palette)
    {
        Volatile.Write(ref _palette, palette);
        _commands.Writer.TryWrite(new("palette"));
    }

    internal async Task<bool> FlushAsync()
    {
        if (!IsReady) return true;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref _nextDrain);
        lock (_drains) _drains.Add(id, completion);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await _commands.Writer.WriteAsync(new("flush", Id: id), timeout.Token).ConfigureAwait(false);
            await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false); return true;
        }
        catch (Exception error) when (FilePackDropWire.IsExpected(error)) { return false; }
        finally { lock (_drains) _drains.Remove(id); }
    }

    private async Task ReadAsync(Stream pipe)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await FilePackDropWire.ReadAsync(pipe, _stop.Token).ConfigureAwait(false);
                if (message.Kind == "files" && message.Paths is { Length: > 0 and <= 100 } paths &&
                    FilePackRequestBroker.TryReadArguments(["--file-pack-load", .. paths], out var normalized))
                    await _receive(normalized).ConfigureAwait(false);
                else if (message.Kind == "drained")
                { lock (_drains) { if (_drains.TryGetValue(message.Id, out var completion)) completion.TrySetResult(); } }
                else throw new InvalidDataException("拖放接收端请求无效。");
            }
        }
        catch (Exception error) when (FilePackDropWire.IsExpected(error)) { ConnectionFailed(); }
        finally { FailDrains(); }
    }

    private async Task WriteAsync(Stream pipe)
    {
        try
        {
            await foreach (var command in _commands.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await FilePackDropWire.WriteAsync(pipe, FilePackDropWire.Encode(Volatile.Read(ref _palette)), timeout.Token).ConfigureAwait(false);
                if (command.Kind != "palette")
                    await FilePackDropWire.WriteAsync(pipe, FilePackDropWire.Encode(command), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (FilePackDropWire.IsExpected(error)) { ConnectionFailed(); }
    }

    private void ConnectionFailed()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel(); _pipe?.Dispose(); FailDrains(); _failure();
    }

    private void FailDrains()
    { lock (_drains) foreach (var completion in _drains.Values) completion.TrySetCanceled(); }

    public ValueTask DisposeAsync()
    { lock (_disposeGate) return new(_disposing ??= DisposeCoreAsync()); }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _commands.Writer.TryComplete(); _stop.Cancel(); FailDrains();
            // Detachment belongs to HwndHost; the child destroys its own window.
            _pipe?.Dispose(); Handle = IntPtr.Zero;
        }
        finally
        {
            // Even an unexpected launch/receiver exception must not bypass cleanup.
            try
            {
                try { await ObserveShutdownAsync(_starting).ConfigureAwait(false); }
                finally { await ObserveShutdownAsync(Task.WhenAll(_reader, _writer)).ConfigureAwait(false); }
            }
            finally
            {
                var process = _process;
                try { if (process is not null) await StopProcessAsync(process).ConfigureAwait(false); }
                finally
                {
                    try { _pipe?.Dispose(); }
                    finally
                    {
                        try { process?.Dispose(); }
                        finally
                        {
                            Handle = IntPtr.Zero; _pipe = null; _process = null;
                            Volatile.Write(ref _expectedProcessId, 0); _stop.Dispose();
                        }
                    }
                }
            }
        }
    }

    private static async Task ObserveShutdownAsync(Task task)
    { try { await task.ConfigureAwait(false); } catch (Exception error) when (FilePackDropWire.IsExpected(error)) { } }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Start retains this exact process handle; never terminate by name or
            // reopen an arbitrary process ID after the original helper has exited.
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or Win32Exception or InvalidOperationException) { }
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
