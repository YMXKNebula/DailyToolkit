using System.IO;
using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace DailyToolkit.Desktop.FilePack;

// Local, same-user IPC only. Explorer invocations join the existing instance;
// requests are bounded and acknowledged before any UI collection starts.
internal sealed class FilePackRequestBroker : IDisposable, IAsyncDisposable
{
    private const int MaximumBytes = 128 * 1024;
    private const int MaximumConnections = 8;
    private static readonly uint SessionId = ReadSessionId();
    private readonly string _name;
    private readonly Func<IReadOnlyList<string>, Task> _receive;
    private readonly Action<string>? _failure;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<string[]> _requests = Channel.CreateBounded<string[]>(new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = false });
    private readonly Task _server, _consumer;
    private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _pipes = new();
    private int _disposed;
    internal static string PipeName
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return $"DailyToolkit.FilePack.{identity.User!.Value}.{SessionId}"; }
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
            // CurrentUserOnly compares the token's default owner, which changes
            // from the user SID to Administrators when the same user elevates.
            // Compare the actual user instead, without giving the server a token
            // it can use to impersonate this Explorer invocation.
            await using var client = new NamedPipeClientStream(".", name ?? PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var identity = WindowsIdentity.GetCurrent();
            if (client.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != identity.User ||
                !GetNamedPipeServerSessionId(client.SafePipeHandle, out var session) || session != SessionId)
                return false;
            await client.WriteAsync(BitConverter.GetBytes(bytes.Length), timeout.Token).ConfigureAwait(false);
            await client.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
            await client.FlushAsync(timeout.Token).ConfigureAwait(false);
            var ack = new byte[1]; await client.ReadExactlyAsync(ack, timeout.Token).ConfigureAwait(false); return ack[0] == 1;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }
    private async Task ListenAsync()
    {
        var sessions = new HashSet<Task>();
        try
        {
            var security = CreatePipeSecurity();
            while (!_stop.IsCancellationRequested)
            {
                foreach (var completed in sessions.Where(task => task.IsCompleted).ToArray())
                { await completed.ConfigureAwait(false); sessions.Remove(completed); }
                if (sessions.Count == MaximumConnections)
                {
                    await Task.WhenAny(sessions).WaitAsync(_stop.Token).ConfigureAwait(false);
                    continue;
                }
                // Keep one idle acceptor. Only connected clients create sessions,
                // so a slow acknowledgement reader cannot hold up Explorer's burst.
                NamedPipeServerStream? pending = NamedPipeServerStreamAcl.Create(_name, PipeDirection.InOut, MaximumConnections,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
                try
                {
                    _pipes.TryAdd(pending, 0);
                    await pending.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    sessions.Add(ReceiveAsync(pending));
                    pending = null; // The session now owns disposal.
                }
                finally
                {
                    if (pending is not null) { _pipes.TryRemove(pending, out _); pending.Dispose(); }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or UnauthorizedAccessException)
        { if (!_stop.IsCancellationRequested) _failure?.Invoke("文件集装箱右键入口未能启动。"); }
        finally
        {
            try { await Task.WhenAll(sessions).ConfigureAwait(false); }
            finally { _requests.Writer.TryComplete(); }
        }
    }
    private async Task ReceiveAsync(NamedPipeServerStream pipe)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var header = new byte[4]; await pipe.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            var length = BitConverter.ToInt32(header); if (length is < 2 or > MaximumBytes) return;
            var bytes = new byte[length]; await pipe.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
            var paths = JsonSerializer.Deserialize<string[]>(bytes);
            var valid = GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var session) && session == SessionId &&
                paths is not null && TryReadArguments(["--file-pack-load", .. paths], out var normalized) && _requests.Writer.TryWrite(normalized);
            await pipe.WriteAsync(new byte[] { valid ? (byte)1 : (byte)0 }, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            // Preserve unread acknowledgements until the client closes or its
            // deadline expires, without blocking unrelated connections.
            await pipe.ReadAsync(new byte[1], timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or JsonException or ArgumentException) { }
        finally { _pipes.TryRemove(pipe, out _); await pipe.DisposeAsync().ConfigureAwait(false); }
    }
    internal static PipeSecurity CreatePipeSecurity()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("当前用户身份不可用。");
        var security = new PipeSecurity();
        // Keep this endpoint exclusive to the actual user across their normal
        // and administrator tokens. Do not grant the Administrators group or
        // inherit permissions from a broader default owner ACL.
        security.SetSecurityDescriptorSddlForm($"O:{user.Value}D:P(A;;FA;;;{user.Value})");
        return security;
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
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel(); foreach (var pipe in _pipes.Keys) pipe.Dispose();
    }
    public async ValueTask DisposeAsync() { Dispose(); await Task.WhenAll(_server, _consumer).ConfigureAwait(false); _stop.Dispose(); }
    private static uint ReadSessionId()
    { using var process = System.Diagnostics.Process.GetCurrentProcess(); return (uint)process.SessionId; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint session);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);
}
