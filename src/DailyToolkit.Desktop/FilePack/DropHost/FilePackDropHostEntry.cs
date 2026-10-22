using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace DailyToolkit.Desktop.FilePack.DropHost;

// This branch runs before Application/Theme initialization. It receives paths;
// collection, compression and all privileged file access stay in the main process.
internal static class FilePackDropHostEntry
{
    internal static int Run(string[] args)
    {
        if (!FilePackDropProcess.TryReadArguments(args, out var name, out var parentId) || !FilePackDropProcess.IsStandardProcess) return 2;
        var executable = System.Environment.ProcessPath;
        using var process = Process.GetCurrentProcess();
        if (executable is null || !FilePackDropProcess.IsTrustedPeer((uint)parentId, executable, process.SessionId, standard: false)) return 3;
        using var stop = new CancellationTokenSource();
        var outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = false });
        var acceptingGate = new object();
        var accepting = 0;
        try
        {
            using var window = new FilePackNativeDropWindow(paths =>
            {
                // A pause takes this same lock, so its drain marker always follows
                // every path batch for which the OLE target reported Copy.
                lock (acceptingGate)
                {
                    if (accepting == 0) return false;
                    try { return outgoing.Writer.TryWrite(FilePackDropWire.Encode(new("files", Paths: paths))); }
                    catch (Exception error) when (FilePackDropWire.IsExpected(error)) { return false; }
                }
            });
            var pump = PumpAsync(name, parentId, window, outgoing, value => { lock (acceptingGate) accepting = value ? 1 : 0; }, stop.Token);
            try { window.Run(); }
            finally
            {
                lock (acceptingGate) accepting = 0;
                outgoing.Writer.TryComplete(); stop.Cancel();
                try { pump.GetAwaiter().GetResult(); } catch (Exception error) when (FilePackDropWire.IsExpected(error)) { }
            }
            return 0;
        }
        catch (Exception error) when (FilePackDropWire.IsExpected(error) || error is COMException) { return 1; }
    }

    internal static async Task PumpAsync(string name, int parentId, FilePackNativeDropWindow window,
        Channel<byte[]> outgoing, Action<bool> enable, CancellationToken stop)
    {
        // Keep synchronous named-pipe connection attempts off the native UI thread.
        await Task.Yield();
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        Task writer = Task.CompletedTask;
        Task parentExit = Task.CompletedTask;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        try
        {
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            using var identity = WindowsIdentity.GetCurrent();
            if (pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) != identity.User ||
                !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var serverId) || serverId != parentId)
                throw new UnauthorizedAccessException("拖放接收端身份不匹配。");
            var initial = await FilePackDropWire.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
            if (initial.Kind != "palette") throw new InvalidOperationException("拖放接收端初始化无效。");
            window.SetPalette(initial.Background, initial.Foreground, initial.Border); enable(initial.Enabled);
            await FilePackDropWire.WriteAsync(pipe, FilePackDropWire.Encode(new("ready", Window: window.Handle.ToInt64())), timeout.Token).ConfigureAwait(false);
            writer = WriteAsync(pipe, outgoing.Reader, lifetime.Token);
            _ = writer.ContinueWith(task => { if (task.IsFaulted) window.RequestStop(); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            parentExit = ObserveParentAsync(parentId, window, lifetime.Token);
            while (!stop.IsCancellationRequested)
            {
                var command = await FilePackDropWire.ReadAsync(pipe, stop).ConfigureAwait(false);
                if (command.Kind == "palette") { window.SetPalette(command.Background, command.Foreground, command.Border); enable(command.Enabled); }
                else if (command.Kind == "flush")
                {
                    enable(false);
                    await outgoing.Writer.WriteAsync(FilePackDropWire.Encode(new("drained", Id: command.Id)), stop).ConfigureAwait(false);
                }
                else throw new InvalidOperationException("拖放接收端指令无效。");
            }
        }
        catch (Exception error) when (FilePackDropWire.IsExpected(error)) { }
        finally
        {
            enable(false); outgoing.Writer.TryComplete(); lifetime.Cancel(); window.RequestStop();
            try { await Task.WhenAll(writer, parentExit).ConfigureAwait(false); } catch (Exception error) when (FilePackDropWire.IsExpected(error)) { }
        }
    }

    private static async Task WriteAsync(Stream pipe, ChannelReader<byte[]> reader, CancellationToken stop)
    {
        await foreach (var bytes in reader.ReadAllAsync(stop).ConfigureAwait(false))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop); timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await FilePackDropWire.WriteAsync(pipe, bytes, timeout.Token).ConfigureAwait(false);
        }
    }

    private static async Task ObserveParentAsync(int parentId, FilePackNativeDropWindow window, CancellationToken stop)
    {
        try
        {
            using var parent = Process.GetProcessById(parentId);
            await parent.WaitForExitAsync(stop).ConfigureAwait(false);
        }
        catch (ArgumentException) { /* The verified parent has already exited. */ }
        window.RequestStop();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}
