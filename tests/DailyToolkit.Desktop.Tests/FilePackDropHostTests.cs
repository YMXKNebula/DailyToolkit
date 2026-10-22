using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Desktop.FilePack;
using DailyToolkit.Desktop.FilePack.DropHost;
using DailyToolkit.Desktop.Presentation;
using Microsoft.Win32.SafeHandles;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckFilePackDropHostAsync()
    {
        Require(ClipboardTestStation.Name.Length > 0 && ClipboardTestStation.Desktop.Length > 0 &&
            !ClipboardTestStation.Name.Equals("WinSta0", StringComparison.OrdinalIgnoreCase),
            "Drop host checks require the existing private test window station");
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-filepack-drop-host-");
        try
        {
            var source = Path.Combine(fixture.FullName, "保留 原文件.txt");
            await File.WriteAllTextAsync(source, "native drop fixture - source retained");
            await CheckFilePackNativeDropHostAsync(source);
            await CheckFilePackDropBridgeAsync(fixture.FullName, source);
            await CheckFilePackWindowDropLifetimeAsync(fixture.FullName, source);
            Require(await File.ReadAllTextAsync(source) == "native drop fixture - source retained", "Drop host checks changed their input file");
            Console.WriteLine("PASS FilePack native HWND/OLE registration, actual receiver CF_HDROP callbacks and bounded copies; production child hosting/DPI, concurrent independent bridges/Pump/wire, delivery-before-drained, pause/resume and shutdown/cancellation cleanup on a private station. Worker queue injection is synthetic, same integrity only; no physical drag, Explorer, UAC or input injection.");
        }
        finally { FilePackPaths.EnsureNoReparsePoints(fixture.FullName); fixture.Delete(recursive: true); }
    }

    private static async Task CheckFilePackNativeDropHostAsync(string source)
    {
        List<string[]> received = [];
        var accept = true;
        using var receiver = new FilePackNativeDropWindow(paths =>
        {
            if (!accept) return false;
            received.Add(paths); return true;
        });
        var handle = receiver.Handle;
        Require(handle != IntPtr.Zero && DropHostTestNative.IsWindow(handle) && !DropHostTestNative.IsWindowVisible(handle),
            "Native drop receiver was absent or shown before receiving its palette");
        var dummy = new DropHostDummyTarget();
        var dummyPointer = Marshal.GetComInterfaceForObject<DropHostDummyTarget, IDropHostTestTarget>(dummy);
        try
        {
            Require(DropHostTestNative.RegisterDragDrop(handle, dummyPointer) == unchecked((int)0x80040101),
                "The actual native HWND was not already registered with OLE");
        }
        finally { Marshal.Release(dummyPointer); }

        var dark = ThemePalette.Dark;
        receiver.SetPalette(DropHostRgb(dark.Surface), DropHostRgb(dark.Text), DropHostRgb(dark.Border));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        using var surface = new FilePackDropSurface(handle) { Height = 40, Focusable = false };
        var window = new Window { Content = surface, Width = 480, Height = 160, ShowActivated = false, ShowInTaskbar = false };
        try
        {
            window.Show(); window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var parent = new WindowInteropHelper(window).Handle;
            Require(surface.Handle == handle && DropHostTestNative.GetParent(handle) == parent &&
                (DropHostTestNative.GetWindowLong(handle, -16) & 0x40000000) != 0,
                "Production HwndHost did not attach the real native receiver as a child");
            var childDpi = DropHostTestNative.GetWindowDpiAwarenessContext(handle);
            var parentDpi = DropHostTestNative.GetWindowDpiAwarenessContext(parent);
            Require(DropHostTestNative.AreDpiAwarenessContextsEqual(childDpi, parentDpi) &&
                DropHostTestNative.AreDpiAwarenessContextsEqual(childDpi, new IntPtr(-4)),
                "Receiver and parent did not retain the same PerMonitorV2 awareness");
            _ = DropHostTestNative.UpdateWindow(handle);
            Require(DropHostTestField(receiver, "_palette") is not null &&
                (nint)DropHostTestField(receiver, "_backgroundBrush")! != 0 &&
                (nint)DropHostTestField(receiver, "_borderBrush")! != 0,
                "Native receiver did not apply the supplied application palette");
            TryCaptureDropHostChild(handle, (uint)System.Environment.ProcessId, "drop-native-dark.png",
                new("palette", Background: DropHostRgb(dark.Surface), Foreground: DropHostRgb(dark.Text), Border: DropHostRgb(dark.Border)));

            var actualTarget = DropHostTestField(receiver, "_dropTarget")!;
            var targetInterface = actualTarget.GetType().GetInterfaces().Single(type => type.GUID == new Guid("00000122-0000-0000-C000-000000000046"));
            var files = new DropHostDataObject([source, source]);
            Require(InvokeDropHostTarget(actualTarget, targetInterface, "DragEnter", files, 3) == 1 &&
                InvokeDropHostTarget(actualTarget, targetInterface, "DragOver", files, 3) == 1 &&
                InvokeDropHostTarget(actualTarget, targetInterface, "Drop", files, 3) == 1 &&
                received.Count == 1 && received[0].SequenceEqual([source]),
                "The actual registered receiver did not accept Copy and remove duplicate file paths");
            Require(files.GetDataCalls == 1 && DropHostTestNative.GlobalFlags(files.LastMedium) == 0x8000,
                "CF_HDROP STGMEDIUM was not released by the real receiver");

            var moveOnly = new DropHostDataObject([source]);
            Require(InvokeDropHostTarget(actualTarget, targetInterface, "DragEnter", moveOnly, 2) == 0 &&
                InvokeDropHostTarget(actualTarget, targetInterface, "DragOver", moveOnly, 2) == 0 &&
                InvokeDropHostTarget(actualTarget, targetInterface, "Drop", moveOnly, 2) == 0 &&
                moveOnly.GetDataCalls == 0 && received.Count == 1,
                "Move-only drag was accepted or its data was consumed");
            targetInterface.GetMethod("DragLeave")!.Invoke(actualTarget, null);
            Require(InvokeDropHostTarget(actualTarget, targetInterface, "DragOver", files, 3) == 0,
                "DragLeave retained a stale accepted-format state");
            var unsupported = new DropHostDataObject([source], supported: false);
            Require(InvokeDropHostTarget(actualTarget, targetInterface, "DragEnter", unsupported, 3) == 0 &&
                InvokeDropHostTarget(actualTarget, targetInterface, "Drop", unsupported, 3) == 0 && unsupported.GetDataCalls == 0,
                "A non-CF_HDROP object reached the receiver queue");

            void DropCase(string[] paths, bool expected, string description)
            {
                var before = received.Count;
                var data = new DropHostDataObject(paths);
                var effect = InvokeDropHostTarget(actualTarget, targetInterface, "Drop", data, 1);
                Require(effect == (expected ? 1u : 0u) && received.Count == before + (expected ? 1 : 0), description);
                Require(data.GetDataCalls == 1 && DropHostTestNative.GlobalFlags(data.LastMedium) == 0x8000,
                    "A rejected or accepted CF_HDROP medium leaked: " + description);
            }
            DropCase([], false, "An empty file list was accepted");
            DropCase(["relative-file.txt"], false, "A relative file path was accepted");
            DropCase([@"D:\" + new string('a', 32768)], false, "An overlong file path was accepted");
            var hundred = Enumerable.Range(0, 100).Select(index => Path.Combine(Path.GetDirectoryName(source)!, "not-read-" + index + ".txt")).ToArray();
            DropCase(hundred, true, "A bounded 100-path list was rejected");
            Require(received[^1].Length == 100, "The 100-path list was truncated");
            DropCase([.. hundred, Path.Combine(Path.GetDirectoryName(source)!, "101.txt")], false, "More than 100 paths were accepted");
            var nearLimit = Enumerable.Range(0, 20).Select(index => @"D:\" + string.Join('\\', Enumerable.Repeat(new string('a', 200), 32)) + "\\" + index).ToArray();
            DropCase(nearLimit, true, "Valid absolute paths below the UTF8 byte limit were rejected");
            var overLimit = Enumerable.Range(0, 40).Select(index => @"D:\" + string.Join('\\', Enumerable.Repeat(new string('文', 200), 6)) + "\\" + index).ToArray();
            DropCase(overLimit, false, "A file list exceeding 128 KiB of UTF8 paths was accepted");
            accept = false;
            DropCase([source], false, "The receiver reported Copy after its local bounded queue rejected the paths");
            Require(await File.ReadAllTextAsync(source) == "native drop fixture - source retained", "Native drop modified the original input");
        }
        finally
        {
            surface.Dispose(); window.Content = null; window.Close(); receiver.Dispose();
        }
        Require(receiver.Handle == IntPtr.Zero && !DropHostTestNative.IsWindow(handle) &&
            (nint)DropHostTestField(receiver, "_font")! == 0 &&
            (nint)DropHostTestField(receiver, "_backgroundBrush")! == 0 &&
            (nint)DropHostTestField(receiver, "_borderBrush")! == 0 &&
            (nint)DropHostTestField(receiver, "_dropTargetInterface")! == 0 &&
            !(bool)DropHostTestField(receiver, "_oleInitialized")!,
            "Native receiver disposal retained a window or owned OLE/GDI resource");
    }

    private static object? DropHostTestField(object value, string name) =>
        value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value);

    private static uint InvokeDropHostTarget(object target, Type contract, string method, ComDataObject data, uint effect)
    {
        var member = contract.GetMethod(method)!;
        var pointIndex = method == "DragOver" ? 1 : 2;
        var point = Activator.CreateInstance(member.GetParameters()[pointIndex].ParameterType)!;
        object[] arguments = method == "DragOver" ? [0u, point, effect] : [data, 0u, point, effect];
        Require((int)member.Invoke(target, arguments)! == 0, "Native drop callback returned a failing HRESULT: " + method);
        return (uint)arguments[^1];
    }

    private static uint DropHostRgb(string color) => uint.Parse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static async Task CheckFilePackDropBridgeAsync(string fixture, string source)
    {
        using var modelHost = ResourceHost(Path.Combine(fixture, "model"));
        var model = modelHost.FilePack; await model.SettingsReady;
        Require(model.SetShortcut(null, []), "Drop bridge fixture failed to remove its configured shortcut");
        await model.InitializeAsync();
        Require(model.CanEditDraft, "Drop bridge fixture could not initialize the original FilePack model");
        var palette = new FilePackDropMessage("palette", Background: DropHostRgb(ThemePalette.Dark.Surface),
            Foreground: DropHostRgb(ThemePalette.Dark.Text), Border: DropHostRgb(ThemePalette.Dark.Border), Enabled: true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivered = 0;
        var failures = 0;
        var worker = new DropHostWorkerFixture(source);
        var dispatcher = Application.Current.Dispatcher;
        nint completedHandle = 0;
        await using (var bridge = new FilePackDropBridge(async paths =>
        {
            entered.TrySetResult(); await release.Task.ConfigureAwait(false);
            await dispatcher.InvokeAsync(() => model.AddPathsAsync(paths)).Task.Unwrap().ConfigureAwait(false);
            Interlocked.Exchange(ref delivered, 1);
        }, () => Interlocked.Increment(ref failures), palette, worker.Start, worker.Trust, System.Environment.ProcessPath))
        {
            try
            {
                await bridge.StartAsync().WaitAsync(TimeSpan.FromSeconds(12));
                completedHandle = bridge.Handle;
                Require(bridge.IsReady && DropHostTestNative.IsWindow(completedHandle), "Cross-process bridge did not retain its worker's real native window");
                await worker.WaitEnabledAsync(true);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var flush = bridge.FlushAsync();
                Require(await Task.WhenAny(flush, Task.Delay(120)) != flush && Volatile.Read(ref delivered) == 0,
                    "Flush reported drained before the original model received the queued file list");
                await worker.WaitEnabledAsync(false);
                Require(!flush.IsCompleted && Volatile.Read(ref delivered) == 0,
                    "Flush resumed or completed while the original model delivery was still blocked");
                release.TrySetResult();
                Require(await flush.WaitAsync(TimeSpan.FromSeconds(10)) && Volatile.Read(ref delivered) == 1 &&
                    model.Entries.Any(row => row.SourcePath == source) && Volatile.Read(ref failures) == 0,
                    "Production Pump/wire/bridge lost queued files or acknowledged drain too early");
                bridge.UpdatePalette(palette with { Enabled = true });
                await worker.WaitEnabledAsync(true);
                Require(bridge.IsReady && Volatile.Read(ref failures) == 0,
                    "A resumed receiver lost its existing bridge connection");
            }
            finally { release.TrySetResult(); }
        }
        await worker.RequireStoppedAsync();
        Require(!DropHostTestNative.IsWindow(completedHandle), "Bridge disposal retained the worker's native window");

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedWorker = new DropHostWorkerFixture("");
        nint closedHandle;
        await using (var bridge = new FilePackDropBridge(_ => Task.CompletedTask, () => disconnected.TrySetResult(), palette,
            closedWorker.Start, closedWorker.Trust, System.Environment.ProcessPath))
        {
            await bridge.StartAsync().WaitAsync(TimeSpan.FromSeconds(12));
            closedHandle = bridge.Handle;
            Require(DropHostTestNative.PostMessage(closedHandle, 0x0010, 0, 0), "Unable to close the test worker's own HWND");
            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await closedWorker.RequireStoppedAsync();
        Require(!DropHostTestNative.IsWindow(closedHandle), "Worker-originated close retained its native window");

        using var launchGate = new ManualResetEventSlim();
        var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledWorker = new DropHostWorkerFixture("");
        var cancelledBridge = new FilePackDropBridge(_ => Task.CompletedTask, () => { }, palette,
            (executable, name, parent) =>
            {
                var process = cancelledWorker.Start(executable, name, parent);
                launched.TrySetResult();
                if (!launchGate.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Drop host cancellation fixture launch gate timed out");
                return process;
            }, cancelledWorker.Trust, System.Environment.ProcessPath);
        var startup = cancelledBridge.StartAsync();
        try
        {
            await launched.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var cleanup = cancelledBridge.DisposeAsync().AsTask();
            launchGate.Set();
            var cancelled = false;
            try { await startup.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception exception) when (FilePackDropWire.IsExpected(exception)) { cancelled = true; }
            Require(cancelled, "Cancelled drop bridge startup succeeded");
            await cleanup.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { launchGate.Set(); await cancelledBridge.DisposeAsync(); }
        // Startup cancellation may precede the child's first connection. The
        // production bridge then bounds cleanup by terminating its own helper.
        await cancelledWorker.RequireStoppedAsync(requireGraceful: false);
        await CheckConcurrentFilePackDropBridgesAsync(palette);
        await model.ShutdownAsync();
        Console.WriteLine("INFO Drop host bridge worker uses the test executable and inherits the current integrity level; launch, delivery and cancellation do not exercise Explorer or a cross-integrity boundary.");
    }

    private static async Task CheckConcurrentFilePackDropBridgesAsync(FilePackDropMessage palette)
    {
        var firstWorker = new DropHostWorkerFixture("");
        var secondWorker = new DropHostWorkerFixture("");
        var failures = 0;
        var first = new FilePackDropBridge(_ => Task.CompletedTask, () => Interlocked.Increment(ref failures), palette,
            firstWorker.Start, firstWorker.Trust, System.Environment.ProcessPath);
        var second = new FilePackDropBridge(_ => Task.CompletedTask, () => Interlocked.Increment(ref failures), palette,
            secondWorker.Start, secondWorker.Trust, System.Environment.ProcessPath);
        nint firstHandle = 0, secondHandle = 0;
        try
        {
            // Both windows stay alive. A process-wide semaphore held until
            // disposal would prevent the second StartAsync from completing.
            await Task.WhenAll(first.StartAsync(), second.StartAsync()).WaitAsync(TimeSpan.FromSeconds(12));
            firstHandle = first.Handle; secondHandle = second.Handle;
            Require(first.IsReady && second.IsReady && firstHandle != secondHandle &&
                DropHostTestNative.IsWindow(firstHandle) && DropHostTestNative.IsWindow(secondHandle),
                "Two independent visible tool windows could not own distinct ready drop receivers concurrently");
            await Task.WhenAll(firstWorker.WaitEnabledAsync(true), secondWorker.WaitEnabledAsync(true));
            var flushed = await Task.WhenAll(first.FlushAsync(), second.FlushAsync()).WaitAsync(TimeSpan.FromSeconds(10));
            Require(flushed.All(value => value) && Volatile.Read(ref failures) == 0,
                "Concurrent independent drop bridges blocked or failed each other's drain");
            await Task.WhenAll(firstWorker.WaitEnabledAsync(false), secondWorker.WaitEnabledAsync(false));
            first.UpdatePalette(palette with { Enabled = true });
            second.UpdatePalette(palette with { Enabled = true });
            await Task.WhenAll(firstWorker.WaitEnabledAsync(true), secondWorker.WaitEnabledAsync(true));
        }
        finally { await Task.WhenAll(first.DisposeAsync().AsTask(), second.DisposeAsync().AsTask()); }
        await Task.WhenAll(firstWorker.RequireStoppedAsync(), secondWorker.RequireStoppedAsync());
        Require(!DropHostTestNative.IsWindow(firstHandle) && !DropHostTestNative.IsWindow(secondHandle),
            "Concurrent drop bridge disposal retained one of its native windows");
    }

    private static async Task CheckFilePackWindowDropLifetimeAsync(string fixture, string source)
    {
        foreach (var quick in new[] { true, false })
        {
            using var host = ResourceHost(Path.Combine(fixture, quick ? "quick-window" : "detailed-window"));
            var model = host.FilePack; await model.SettingsReady;
            Require(model.SetShortcut(null, []), "Window drop lifetime fixture could not remove its configured shortcut");
            await model.InitializeAsync();
            List<DropHostWorkerFixture> workers = [];
            List<FilePackDropBridge> bridges = [];
            var window = new FilePackWindow(model, quick, enableExternalDrop: true, (receive, failure, palette) =>
            {
                // Only the first helper preloads a synthetic path. Restoring
                // the same window must retain its shared draft without adding
                // another copy or invoking a conflict dialog.
                var worker = new DropHostWorkerFixture(workers.Count == 0 ? source : "");
                var bridge = new FilePackDropBridge(receive, failure, palette, worker.Start, worker.Trust, System.Environment.ProcessPath);
                workers.Add(worker); bridges.Add(bridge); return bridge;
            }) { ShowActivated = false, ShowInTaskbar = false };
            var area = (System.Windows.Controls.ContentControl)window.FindName("DropHostArea");
            var closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                window.Show(); window.UpdateLayout();
                var windowHandle = window.Handle;
                await WaitForFilePackAsync(() => area.Content is FilePackDropSurface && area.Visibility == Visibility.Visible,
                    "Original FilePack window failed to attach its external receiver: " + model.Notice);
                Require(workers.Count == 1 && bridges.Count == 1 && bridges[0].IsReady,
                    "Original FilePack window did not own exactly one ready drop bridge");
                var firstSurface = (FilePackDropSurface)area.Content;
                var firstHandle = firstSurface.Handle;
                Require(firstHandle == bridges[0].Handle && DropHostTestNative.GetParent(firstHandle) == windowHandle,
                    "The original window's visible DropHostArea did not contain its worker's actual native HWND");
                await workers[0].WaitEnabledAsync(true);
                await WaitForFilePackAsync(() => model.Entries.Any(row => row.SourcePath == source),
                    "Original FilePack window did not deliver its receiver paths to the existing draft");
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                TryCaptureDropHostChild(firstHandle, (uint)workers[0].ProcessId, quick ? "drop-quick.png" : "drop-detail.png",
                    (FilePackDropMessage)DropHostTestField(bridges[0], "_palette")!);
                Require(await window.FlushDropAsync().WaitAsync(TimeSpan.FromSeconds(10)), "Original FilePack window failed to flush its receiver");
                await workers[0].WaitEnabledAsync(false);
                window.ResumeDrop();
                await workers[0].WaitEnabledAsync(true);

                window.WindowState = WindowState.Minimized;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await window.DropCleanup.WaitAsync(TimeSpan.FromSeconds(12));
                Require(area.Content is null && area.Visibility == Visibility.Collapsed,
                    "Minimizing the original FilePack window retained its external drop surface");
                await workers[0].RequireStoppedAsync();
                Require(!DropHostTestNative.IsWindow(firstHandle), "Minimizing retained the first helper's native HWND");

                window.WindowState = WindowState.Normal;
                await WaitForFilePackAsync(() => workers.Count == 2 && area.Content is FilePackDropSurface && area.Visibility == Visibility.Visible,
                    "Restoring the original FilePack window did not create a replacement receiver: " + model.Notice);
                Require(bridges.Count == 2 && bridges[1].IsReady && !ReferenceEquals(bridges[0], bridges[1]),
                    "Restoring a FilePack window reused its disposed receiver bridge");
                var secondSurface = area.Content as FilePackDropSurface ?? throw new InvalidOperationException("The restored drop surface disappeared");
                var secondHandle = secondSurface.Handle;
                Require(secondHandle == bridges[1].Handle && DropHostTestNative.GetParent(secondHandle) == windowHandle &&
                    model.Entries.Count(row => row.SourcePath == source) == 1,
                    "Restoring a FilePack window detached its new native receiver or lost the shared draft");
                await workers[1].WaitEnabledAsync(true);
                window.CloseRuntime();
                await window.DropCleanup.WaitAsync(TimeSpan.FromSeconds(12));
                await workers[1].RequireStoppedAsync();
                Require(closed && !DropHostTestNative.IsWindow(windowHandle) && !DropHostTestNative.IsWindow(secondHandle) &&
                    area.Content is null && area.Visibility == Visibility.Collapsed,
                    "Closing the original FilePack window retained an owned native window or drop surface");
            }
            finally
            {
                if (!closed) window.CloseRuntime();
                await window.DropCleanup.WaitAsync(TimeSpan.FromSeconds(12));
                // Idempotent fallback is confined to bridges created by this
                // fixture; it cannot reach the user's installed application.
                await Task.WhenAll(bridges.Select(bridge => bridge.DisposeAsync().AsTask()));
                await model.ShutdownAsync();
            }
        }
    }

    // Main must call this BEFORE initializing WPF. The worker joins only the
    // explicitly named private station and desktop created by its parent test.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunFilePackDropHostFixture(string[] args)
    {
        if (args.Length != 6 || args[0] != "--filepack-drop-host-fixture" ||
            !FilePackDropProcess.ValidatePipeName(args[1]) || !int.TryParse(args[2], out var parent) || parent <= 0)
            return 2;
        try
        {
            ClipboardTestStation.Connect(args[3], args[4]);
            var result = 1;
            var thread = new Thread(() =>
            {
                try { ClipboardTestStation.ConnectThread(); result = RunFilePackDropHostFixtureCore(args[1], parent, args[5]); }
                catch (Exception exception) { Console.WriteLine("FAIL isolated drop host worker: " + exception); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join(); return result;
        }
        catch (Exception exception) { Console.WriteLine("FAIL isolated drop host worker station: " + exception); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunFilePackDropHostFixtureCore(string pipe, int parent, string fixturePath)
    {
        using var stop = new CancellationTokenSource();
        var outgoing = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = false });
        var accepting = 0;
        using var window = new FilePackNativeDropWindow(paths => Volatile.Read(ref accepting) != 0 &&
            outgoing.Writer.TryWrite(FilePackDropWire.Encode(new("files", Paths: paths))));
        CheckDropHostWorkerAssembliesAndMemory();
        // Synthetic local queue injection validates the production transport and
        // drain ordering. Native callback parsing is tested independently above.
        if (fixturePath.Length > 0)
        {
            if (!Path.IsPathFullyQualified(fixturePath)) return 2;
            Require(outgoing.Writer.TryWrite(FilePackDropWire.Encode(new("files", Paths: [fixturePath]))), "Worker fixture queue was unexpectedly full");
        }
        var pump = FilePackDropHostEntry.PumpAsync(pipe, parent, window, outgoing, value =>
        {
            Volatile.Write(ref accepting, value ? 1 : 0);
            Console.WriteLine("STATE filepack-drop-enabled=" + value);
        }, stop.Token);
        window.Run();
        Volatile.Write(ref accepting, 0); outgoing.Writer.TryComplete(); stop.Cancel();
        try { pump.GetAwaiter().GetResult(); }
        catch (Exception exception) when (FilePackDropWire.IsExpected(exception)) { }
        Console.WriteLine("PASS isolated drop host worker stopped");
        return 0;
    }

    private sealed class DropHostWorkerFixture(string input)
    {
        private readonly string _image = System.Environment.ProcessPath!;
        private readonly DropHostTestIdentity _identity = ReadDropHostTestIdentity((uint)System.Environment.ProcessId);
        private int _processId;
        private readonly Channel<bool> _enabled = Channel.CreateBounded<bool>(32);
        private Task<string> _output = Task.FromResult("");
        private Task<string> _error = Task.FromResult("");
        internal int ProcessId => _processId;

        internal bool Trust(uint id)
        {
            try
            {
                var peer = ReadDropHostTestIdentity(id);
                return peer.User == _identity.User && peer.Session == _identity.Session &&
                    peer.Integrity == _identity.Integrity && peer.Elevated == _identity.Elevated &&
                    string.Equals(peer.Image, _identity.Image, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(peer.Image, Path.GetFullPath(_image), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception exception) when (FilePackDropWire.IsExpected(exception) || exception is ArgumentException) { return false; }
        }
        internal Process Start(string executable, string pipe, int parent)
        {
            Require(string.Equals(executable, _image, StringComparison.OrdinalIgnoreCase) && Trust((uint)System.Environment.ProcessId),
                "Drop fixture launcher received an unexpected executable or could not validate its own identity");
            if (_identity.Integrity < 0x2000)
            {
                // The sandbox can use a Low-integrity primary token. This
                // test-only same-level launcher must not relax either of the
                // production medium/high authentication branches to admit it.
                Require(!FilePackDropProcess.IsTrustedPeer((uint)System.Environment.ProcessId, _image, checked((int)_identity.Session), true) &&
                    !FilePackDropProcess.IsTrustedPeer((uint)System.Environment.ProcessId, _image, checked((int)_identity.Session), false),
                    "Production peer authentication incorrectly admitted the Low-integrity test runner");
            }
            var start = new ProcessStartInfo(_image)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(_image).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(Program).Assembly.Location);
            foreach (var argument in new[] { "--filepack-drop-host-fixture", pipe, parent.ToString(CultureInfo.InvariantCulture),
                ClipboardTestStation.Name, ClipboardTestStation.Desktop, input }) start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new InvalidOperationException("Isolated drop worker did not start");
            _processId = process.Id;
            _output = CaptureOutputAsync(process.StandardOutput); _error = process.StandardError.ReadToEndAsync();
            return process;
        }

        private async Task<string> CaptureOutputAsync(StreamReader reader)
        {
            var output = new StringBuilder();
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    output.AppendLine(line);
                    const string state = "STATE filepack-drop-enabled=";
                    if (line.StartsWith(state, StringComparison.Ordinal) && bool.TryParse(line.AsSpan(state.Length), out var enabled))
                        Require(_enabled.Writer.TryWrite(enabled), "Drop worker state fixture queue overflowed");
                }
            }
            finally { _enabled.Writer.TryComplete(); }
            return output.ToString();
        }

        internal async Task WaitEnabledAsync(bool expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await _enabled.Reader.WaitToReadAsync(timeout.Token))
                while (_enabled.Reader.TryRead(out var actual))
                    if (actual == expected) return;
            throw new InvalidOperationException("Drop worker exited without expected accepting state: " + expected);
        }

        internal async Task RequireStoppedAsync(bool requireGraceful = true)
        {
            Require(_processId != 0, "Drop worker was never created");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                using var process = Process.GetProcessById(_processId);
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (ArgumentException) { }
            var output = await _output.WaitAsync(timeout.Token);
            var error = await _error.WaitAsync(timeout.Token);
            Require(!output.Contains("FAIL", StringComparison.Ordinal) && error.Length == 0 &&
                (!requireGraceful || output.Contains("PASS isolated drop host worker stopped", StringComparison.Ordinal)),
                "Isolated drop worker reported a failure: " + output + error);
        }
    }

    private sealed record DropHostTestIdentity(SecurityIdentifier User, uint Session, uint Integrity, bool Elevated, string Image);

    private static void CheckDropHostWorkerAssembliesAndMemory()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetName().Name).ToArray();
        var wpf = assemblies.Where(name => name is "PresentationFramework" or "PresentationCore").ToArray();
        Require(wpf.Length == 0, "Native fixture branch loaded WPF assemblies: " + string.Join(", ", wpf));
        using var process = DropHostTestNative.OpenProcess(0x1000, false, (uint)System.Environment.ProcessId);
        var counters = new DropHostTestNative.MemoryCounters { Size = (uint)Marshal.SizeOf<DropHostTestNative.MemoryCounters>() };
        if (!process.IsInvalid && DropHostTestNative.GetProcessMemoryInfo(process, ref counters, counters.Size))
            Console.WriteLine($"INFO isolated same-integrity test worker after native initialization: WPF assemblies=0, privateWorkingSetBytes={counters.PrivateWorkingSet}, privateCommitBytes={counters.PrivateUsage}; fixture measurement only, not an installed-product helper profile.");
        else Console.WriteLine("INFO native fixture WPF assemblies=0; private working-set measurement unavailable on this runner, Win32=" + Marshal.GetLastWin32Error());
    }

    // Optional capture reads only the explicitly owned native child. A private
    // station may have no display pixels: unsupported/blank captures are marked
    // SKIP, never recreated from palette values or presented as actual output.
    private static void TryCaptureDropHostChild(nint child, uint owner, string name, FilePackDropMessage palette)
    {
        var requested = System.Environment.GetEnvironmentVariable("DAILYTOOLKIT_DROP_HOST_IMAGES");
        if (string.IsNullOrWhiteSpace(requested)) return;
        Require(Path.IsPathFullyQualified(requested), "Native capture output root must be an explicit absolute directory");
        var root = Path.GetFullPath(requested);
        var output = Path.GetFullPath(Path.Combine(root, name));
        Require(FilePackPaths.IsWithin(output, root) && Path.GetFileName(name) == name,
            "Native capture path escaped its explicitly selected output root");
        FilePackPaths.EnsureNoReparsePoints(root); FilePackPaths.EnsureNoReparsePoints(output);
        var station = DropHostTestNative.GetProcessWindowStation();
        Require(station != IntPtr.Zero && ReadDropHostObjectName(station) == ClipboardTestStation.Name &&
            !ClipboardTestStation.Name.Equals("WinSta0", StringComparison.OrdinalIgnoreCase),
            "Native capture requires the test's own private window station");
        var thread = DropHostTestNative.GetWindowThreadProcessId(child, out var actualOwner);
        Require(child != IntPtr.Zero && owner != 0 && thread != 0 && actualOwner == owner && DropHostTestNative.IsWindow(child) &&
            ReadDropHostObjectName(DropHostTestNative.GetThreadDesktop(thread)) == ClipboardTestStation.Desktop,
            "Native capture refused a window outside its own process/worker and private desktop");
        nint source = 0, memory = 0, bitmap = 0, previous = 0;
        try
        {
            Require(DropHostTestNative.GetClientRect(child, out var area), "Native capture could not read its child client rectangle");
            var width = area.Right - area.Left; var height = area.Bottom - area.Top;
            Require(width is > 0 and <= 4096 && height is > 0 and <= 512,
                "Native capture child size exceeded the bounded strip area");
            if (!DropHostTestNative.InvalidateRect(child, IntPtr.Zero, false) ||
                DropHostTestNative.SendMessageTimeout(child, 0x000F, 0, 0, 0x0023, 1000, out _) == IntPtr.Zero)
                throw new NotSupportedException("the private child did not complete a bounded WM_PAINT");
            source = DropHostTestNative.GetDC(child);
            if (source == IntPtr.Zero) throw new NotSupportedException("GetDC(child) is unavailable on this private station");
            var clipType = DropHostTestNative.GetClipBox(source, out var clip);
            if (clipType is 0 or 1 || clip.Right <= clip.Left || clip.Bottom <= clip.Top)
                throw new NotSupportedException("the private child has no nonempty display clipping region");
            memory = DropHostTestNative.CreateCompatibleDC(source);
            if (memory == IntPtr.Zero) throw new NotSupportedException("a compatible capture DC is unavailable");
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            var info = new DropHostTestNative.BitmapInfo
            {
                Header = new() { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32, ImageBytes = (uint)pixels.Length }
            };
            bitmap = DropHostTestNative.CreateDIBSection(source, ref info, 0, out var bits, 0, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new NotSupportedException("the bounded capture DIB could not be created");
            previous = DropHostTestNative.SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new NotSupportedException("the capture DIB could not be selected");
            if (!DropHostTestNative.BitBlt(memory, 0, 0, width, height, source, 0, 0, 0x00CC0020) || !DropHostTestNative.GdiFlush())
                throw new NotSupportedException("the private child pixel copy is not supported");
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            uint Pixel(int x, int y)
            {
                var offset = y * stride + x * 4;
                return (uint)(pixels[offset + 2] << 16 | pixels[offset + 1] << 8 | pixels[offset]);
            }
            var background = 0; var borders = 0; var glyphs = 0;
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var color = Pixel(x, y);
                    if (x == 0 || x == width - 1 || y == 0 || y == height - 1)
                    { if (color == palette.Border) borders++; }
                    else if (color == palette.Background) background++;
                    else if (x > width / 8 && x < width * 7 / 8 && y > height / 8 && y < height * 7 / 8 &&
                        color != palette.Border && DropHostPixelDistance(color, palette.Foreground) < DropHostPixelDistance(color, palette.Background)) glyphs++;
                }
            if (background < width * height / 4 || borders < width || glyphs < 8)
                throw new NotSupportedException($"actual background/border/central glyph pixels were unavailable (background={background}, border={borders}, glyph={glyphs})");
            var image = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
                System.Windows.Media.PixelFormats.Bgr32, null, pixels, stride);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var encoded = new MemoryStream(); encoder.Save(encoded);
            Directory.CreateDirectory(root); FilePackPaths.EnsureNoReparsePoints(root);
            using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            encoded.Position = 0; encoded.CopyTo(file);
            Console.WriteLine($"INFO native child pixels captured: {output}, {width}x{height}, background={background}, border={borders}, glyph={glyphs}; private-station capture, not physical display validation.");
        }
        catch (NotSupportedException exception)
        { Console.WriteLine($"SKIP native strip visual {name}: {exception.Message}; no substitute image was generated."); }
        finally
        {
            var cleaned = true;
            if (previous != IntPtr.Zero && previous != new IntPtr(-1))
                cleaned &= DropHostTestNative.SelectObject(memory, previous) != IntPtr.Zero;
            if (bitmap != IntPtr.Zero) cleaned &= DropHostTestNative.DeleteObject(bitmap);
            if (memory != IntPtr.Zero) cleaned &= DropHostTestNative.DeleteDC(memory);
            if (source != IntPtr.Zero) cleaned &= DropHostTestNative.ReleaseDC(child, source) == 1;
            Require(cleaned, "Native capture GDI/DC cleanup failed");
        }
    }

    private static int DropHostPixelDistance(uint first, uint second) =>
        Math.Abs((int)(first >> 16 & 255) - (int)(second >> 16 & 255)) +
        Math.Abs((int)(first >> 8 & 255) - (int)(second >> 8 & 255)) + Math.Abs((int)(first & 255) - (int)(second & 255));

    private static string ReadDropHostObjectName(nint handle)
    {
        var name = new StringBuilder(256);
        Require(handle != IntPtr.Zero && DropHostTestNative.GetUserObjectInformation(handle, 2, name, 512, out _),
            "Native capture could not verify its private station/desktop name");
        return name.ToString();
    }

    // Reuse the production primary-token query without its deliberate
    // medium/high policy split. Fixtures must inherit the exact same level,
    // user, session, elevation and executable as this restricted test runner.
    private static DropHostTestIdentity ReadDropHostTestIdentity(uint processId)
    {
        using var process = DropHostTestNative.OpenProcess(0x1000, false, processId);
        if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to query the isolated drop fixture process");
        if (!DropHostTestNative.GetExitCodeProcess(process, out var code) || code != 259)
            throw new InvalidOperationException("The isolated drop fixture process is not running");
        var query = typeof(FilePackDropProcess).GetMethod("ReadIdentity", BindingFlags.NonPublic | BindingFlags.Static)!;
        object identity;
        try { identity = query.Invoke(null, [process])!; }
        catch (TargetInvocationException exception)
        { throw new InvalidOperationException("The production identity query failed for an isolated fixture", exception.InnerException ?? exception); }
        T Value<T>(string name) => (T)identity.GetType().GetProperty(name)!.GetValue(identity)!;
        return new(Value<SecurityIdentifier>("User"), Value<uint>("Session"), Value<uint>("Integrity"),
            Value<bool>("Elevated"), Path.GetFullPath(Value<string>("Image")));
    }

    // This data object owns only fresh HGLOBAL allocations handed to the real
    // receiver. ReleaseStgMedium, rather than this fixture, must free each one.
    private sealed class DropHostDataObject(string[] paths, bool supported = true) : ComDataObject
    {
        internal int GetDataCalls { get; private set; }
        internal IntPtr LastMedium { get; private set; }
        public int QueryGetData(ref FORMATETC format) => supported && format.cfFormat == 15 &&
            format.tymed == TYMED.TYMED_HGLOBAL && format.dwAspect == DVASPECT.DVASPECT_CONTENT ? 0 : unchecked((int)0x80040064);
        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            if (QueryGetData(ref format) != 0) throw new COMException("Unsupported fixture format", unchecked((int)0x80040064));
            var content = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
            var bytes = new byte[20 + content.Length];
            BitConverter.GetBytes(20).CopyTo(bytes, 0); BitConverter.GetBytes(1).CopyTo(bytes, 16); content.CopyTo(bytes, 20);
            var memory = DropHostTestNative.GlobalAlloc(0x42, (nuint)bytes.Length);
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var locked = DropHostTestNative.GlobalLock(memory);
            if (locked == IntPtr.Zero) { DropHostTestNative.GlobalFree(memory); throw new Win32Exception(Marshal.GetLastWin32Error()); }
            try { Marshal.Copy(bytes, 0, locked, bytes.Length); }
            finally { DropHostTestNative.GlobalUnlock(memory); }
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = memory, pUnkForRelease = null };
            LastMedium = memory; GetDataCalls++;
        }
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC format, out FORMATETC output) { output = format; output.ptd = IntPtr.Zero; return 0x00040130; }
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out IEnumSTATDATA enumerator) { enumerator = null!; return unchecked((int)0x80040003); }
    }

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropHostTestTarget
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] ComDataObject data, uint keys, long point, ref uint effect);
        [PreserveSig] int DragOver(uint keys, long point, ref uint effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] ComDataObject data, uint keys, long point, ref uint effect);
    }
    private sealed class DropHostDummyTarget : IDropHostTestTarget
    {
        public int DragEnter(ComDataObject data, uint keys, long point, ref uint effect) { effect = 0; return 0; }
        public int DragOver(uint keys, long point, ref uint effect) { effect = 0; return 0; }
        public int DragLeave() => 0;
        public int Drop(ComDataObject data, uint keys, long point, ref uint effect) { effect = 0; return 0; }
    }

    private static class DropHostTestNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Rectangle { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfoHeader
        {
            internal uint Size;
            internal int Width, Height;
            internal ushort Planes, Bits;
            internal uint Compression, ImageBytes;
            internal int XResolution, YResolution;
            internal uint Colors, ImportantColors;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo { internal BitmapInfoHeader Header; internal uint Color; }
        [StructLayout(LayoutKind.Sequential)] internal struct MemoryCounters
        {
            internal uint Size, PageFaults;
            internal nuint PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool;
            internal nuint Pagefile, PeakPagefile, PrivateUsage, PrivateWorkingSet;
            internal ulong SharedCommit;
        }
        [DllImport("ole32.dll")] internal static extern int RegisterDragDrop(IntPtr window, IntPtr target);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] internal static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateWindow(IntPtr window);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(IntPtr window, uint message, nint wParam, nint lParam);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] internal static extern IntPtr GlobalFree(IntPtr memory);
        [DllImport("kernel32.dll")] internal static extern uint GlobalFlags(IntPtr memory);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetProcessMemoryInfo(SafeProcessHandle process, ref MemoryCounters counters, uint size);
        [DllImport("user32.dll")] internal static extern nint GetProcessWindowStation();
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] internal static extern nint GetThreadDesktop(uint threadId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetUserObjectInformation(nint value, int index, StringBuilder name, uint bytes, out uint needed);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint window, out Rectangle rectangle);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InvalidateRect(nint window, nint rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nuint result);
        [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
        [DllImport("gdi32.dll")] internal static extern int GetClipBox(nint dc, out Rectangle rectangle);
        [DllImport("gdi32.dll")] internal static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
        [DllImport("gdi32.dll")] internal static extern nint SelectObject(nint dc, nint value);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GdiFlush();
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(nint dc);
    }
}
