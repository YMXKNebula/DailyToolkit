using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;

namespace DailyToolkit.Desktop.FilePack;

// Local, same-user IPC only. Explorer invocations join the existing instance;
// requests are bounded and acknowledged before any UI collection starts.
internal sealed class FilePackRequestBroker : IDisposable, IAsyncDisposable
{
    private const int MaximumBytes = 128 * 1024;
    private readonly string _name;
    private readonly Func<IReadOnlyList<string>, Task> _receive;
    private readonly Action<string>? _failure;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<string[]> _requests = Channel.CreateBounded<string[]>(new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = true });
    private readonly Task _server, _consumer;
    private NamedPipeServerStream? _pipe;
    private int _disposed;
    internal static string PipeName
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return $"DailyToolkit.FilePack.{identity.User!.Value}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}"; }
    }
    internal FilePackRequestBroker(Func<IReadOnlyList<string>, Task> receive, string? name = null, Action<string>? failure = null)
    { _receive = receive; _failure = failure; _name = name ?? PipeName; _server = ListenAsync(); _consumer = ConsumeAsync(); }
    internal static bool TryReadArguments(string[] args, out string[] paths)
    {
        paths = [];
        if (args.Length is < 2 or > 101 || args[0] != "--file-pack-load") return false;
        try
        {
            if (args.Skip(1).Any(path => path is null || path.Length is 0 or > 32767 || !Path.IsPathFullyQualified(path) || path.Contains('\0'))) return false;
            paths = args.Skip(1).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); return paths.Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { return false; }
    }
    internal static async Task<bool> SendAsync(IReadOnlyList<string> paths, string? name = null)
    {
        if (paths.Count is < 1 or > 100) return false;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(paths); if (bytes.Length > MaximumBytes) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await using var client = new NamedPipeClientStream(".", name ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(BitConverter.GetBytes(bytes.Length), timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
            var ack = new byte[1]; await client.ReadExactlyAsync(ack, timeout.Token).ConfigureAwait(false); return ack[0] == 1;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }
    private async Task ListenAsync()
    {
        try
        {
            await using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _pipe = pipe;
            while (!_stop.IsCancellationRequested)
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    var header = new byte[4]; await pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
                    var length = BitConverter.ToInt32(header); if (length is < 2 or > MaximumBytes) continue;
                    var bytes = new byte[length]; await pipe.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
                    var paths = JsonSerializer.Deserialize<string[]>(bytes);
                    var valid = paths is not null && TryReadArguments(["--file-pack-load", .. paths], out var normalized) && _requests.Writer.TryWrite(normalized);
                    await pipe.WriteAsync(new byte[] { valid ? (byte)1 : (byte)0 }, timeout.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
                    // The client closes after reading its acknowledgement. Keep the
                    // instance alive until then, so a fast reconnect cannot race it.
                    await pipe.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException or ArgumentException) { }
                finally { if (!_stop.IsCancellationRequested) pipe.Disconnect(); }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
        { if (!_stop.IsCancellationRequested) _failure?.Invoke("文件集装箱右键入口未能启动。"); }
        finally { _pipe = null; _requests.Writer.TryComplete(); }
    }
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var paths in _requests.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                var batch = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
                // Explorer can invoke the legacy command separately for each item.
                // Coalesce a short burst without a permanent timer or an unbounded queue.
                await Task.Delay(160, _stop.Token).ConfigureAwait(false);
                while (batch.Count < 100 && _requests.Reader.TryRead(out var next)) batch.UnionWith(next);
                try { await _receive(batch.ToArray()).ConfigureAwait(false); }
                catch (Exception exception) when (FilePackViewModel.IsExpected(exception))
                { _failure?.Invoke("文件未能装入，请重试。"); }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
    }
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) != 0) return; _stop.Cancel(); _pipe?.Dispose(); }
    public async ValueTask DisposeAsync() { Dispose(); await Task.WhenAll(_server, _consumer).ConfigureAwait(false); _stop.Dispose(); }
}
