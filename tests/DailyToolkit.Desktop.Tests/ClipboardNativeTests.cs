using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Desktop.ClipRecall;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Core.Gaming;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    // A window station has its own clipboard. Connect BEFORE creating Application,
    // never switch the input desktop, and fail closed if isolation is unavailable.
    // The process owns these handles until termination (WPF retains hidden HWNDs).
    private static class ClipboardTestStation
    {
        internal static string Name = "", Desktop = "";
        private static IntPtr _station, _desktop;
        internal static void Connect(string? name = null, string? desktop = null)
        {
            _station = name is null ? CreateWindowStation(null, 0, 0x000F037F, IntPtr.Zero) : OpenWindowStation(name, false, 0x000F037F);
            if (_station == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Private test window station unavailable");
            var buffer = new StringBuilder(256);
            if (!GetUserObjectInformation(_station, 2, buffer, 512, out _) || buffer.ToString().Equals("WinSta0", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing clipboard tests on interactive window station");
            Name = buffer.ToString(); Desktop = desktop ?? "ClipRecall-" + Guid.NewGuid().ToString("N");
            if (!SetProcessWindowStation(_station)) throw new Win32Exception(Marshal.GetLastWin32Error());
            _desktop = desktop is null ? CreateDesktop(Desktop, IntPtr.Zero, IntPtr.Zero, 0, 0x000F01FF, IntPtr.Zero) : OpenDesktop(Desktop, 0, false, 0x000F01FF);
            if (_desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        internal static void ConnectThread()
        { if (!SetThreadDesktop(_desktop)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowStation(string? name, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenWindowStation(string name, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetProcessWindowStation(IntPtr station);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenDesktop(string name, uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr value, int index, StringBuilder buffer, uint length, out uint needed);
    }
    private static class ClipboardTestNative
    {
        internal static uint Format(string name) => RegisterClipboardFormat(name);
        internal static void Write(IntPtr owner, params (uint Format, byte[] Bytes)[] data)
        {
            if (!OpenClipboard(owner)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                Require(EmptyClipboard(), "Native fixture empty failed");
                foreach (var item in data)
                {
                    var memory = GlobalAlloc(0x42, (nuint)item.Bytes.Length);
                    Require(memory != IntPtr.Zero, "Native fixture allocation failed");
                    var pointer = GlobalLock(memory);
                    if (pointer == IntPtr.Zero) { GlobalFree(memory); throw new Win32Exception(); }
                    try { Marshal.Copy(item.Bytes, 0, pointer, item.Bytes.Length); } finally { GlobalUnlock(memory); }
                    if (SetClipboardData(item.Format, memory) == IntPtr.Zero) { GlobalFree(memory); throw new Win32Exception(); }
                }
            }
            finally { CloseClipboard(); }
        }
        internal static byte[] Bytes(uint format)
        {
            var opened = false;
            for (var index = 0; index < 10 && !opened; index++) { opened = OpenClipboard(IntPtr.Zero); if (!opened) Thread.Sleep(20); }
            Require(opened, "Native fixture read open failed");
            try
            {
                var memory = GetClipboardData(format); Require(memory != IntPtr.Zero, "Native fixture format missing");
                var bytes = new byte[checked((int)GlobalSize(memory))]; var pointer = GlobalLock(memory);
                Require(pointer != IntPtr.Zero, "Native fixture lock failed");
                try { Marshal.Copy(pointer, bytes, 0, bytes.Length); } finally { GlobalUnlock(memory); }
                return bytes;
            }
            finally { CloseClipboard(); }
        }
        internal static IDisposable Lock(IntPtr owner)
        { Require(OpenClipboard(owner), "Native fixture contention lock failed"); return new ClipboardTestLock(); }
        private sealed class ClipboardTestLock : IDisposable { public void Dispose() => CloseClipboard(); }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")] private static extern bool CloseClipboard();
        [DllImport("user32.dll")] private static extern bool EmptyClipboard();
        [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
        [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string name);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern nuint GlobalSize(IntPtr memory);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    }
    private static HwndSource ClipboardTestWindow() => new(new HwndSourceParameters("ClipRecall isolated fixture")
    { ParentWindow = new IntPtr(-3), WindowStyle = 0, Width = 1, Height = 1 });
    private static async Task RunClipboardWriterAsync(string mode, string? path)
    {
        using var source = ClipboardTestWindow();
        if (mode == "lock")
        {
            ArgumentNullException.ThrowIfNull(path);
            using (ClipboardTestNative.Lock(source.Handle))
            { await File.WriteAllTextAsync(path, "ready"); await Task.Delay(700); }
            return;
        }
        var access = new ClipboardNativeAccess(source.Handle);
        var settings = new ClipboardHistorySettings { IncludeRestoredInWindowsHistory = true };
        if (mode.StartsWith("image-", StringComparison.Ordinal))
        {
            var image = SyntheticClipboardImage(alpha: mode is not ("image-dib" or "image-opaque"));
            switch (mode)
            {
                case "image-png": ClipboardTestNative.Write(source.Handle, (ClipboardTestNative.Format("PNG"), image.Binary!)); break;
                case "image-v5": ClipboardTestNative.Write(source.Handle, (17u, ClipboardImageCodec.Dib(image, true))); break;
                case "image-dib": ClipboardTestNative.Write(source.Handle, (8u, ClipboardImageCodec.Dib(image, false))); break;
                case "image-opaque": ClipboardTestNative.Write(source.Handle, (ClipboardTestNative.Format("PNG"), image.Binary!)); break;
                case "image-forbid": ClipboardTestNative.Write(source.Handle, (ClipboardTestNative.Format("PNG"), image.Binary!),
                    (ClipboardTestNative.Format("CanIncludeInClipboardHistory"), BitConverter.GetBytes(0u))); break;
                case "image-malformed": ClipboardTestNative.Write(source.Handle, (ClipboardTestNative.Format("PNG"), new byte[30])); break;
                case "image-huge": ClipboardTestNative.Write(source.Handle, (ClipboardTestNative.Format("PNG"), new byte[8 * 1048576 + 1])); break;
                default: throw new InvalidOperationException("Unknown image fixture");
            }
            return;
        }
        if (mode == "files-real")
        {
            ArgumentNullException.ThrowIfNull(path);
            Require(access.Restore(ClipboardContent.FromFiles([path]), settings).Result.Success, "Real file-list fixture failed"); return;
        }
        if (mode is "text" or "link" or "files")
        {
            var content = mode switch { "link" => ClipboardContent.FromText("https://example.com/native-fixture"),
                "files" => ClipboardContent.FromFiles(["D:\\native-fixture.txt", "D:\\native-folder"]), _ => ClipboardContent.FromText("native external fixture") };
            Require(access.Restore(content, settings).Result.Success, "Cross-process clipboard write failed"); return;
        }
        var text = (13u, Encoding.Unicode.GetBytes("native privacy fixture\0"));
        switch (mode)
        {
            case "exclude": ClipboardTestNative.Write(source.Handle, text, (ClipboardTestNative.Format("ExcludeClipboardContentFromMonitorProcessing"), [1])); break;
            case "forbid": ClipboardTestNative.Write(source.Handle, text, (ClipboardTestNative.Format("CanIncludeInClipboardHistory"), BitConverter.GetBytes(0u))); break;
            case "cloud": ClipboardTestNative.Write(source.Handle, text, (ClipboardTestNative.Format("CanUploadToCloudClipboard"), BitConverter.GetBytes(0u))); await Task.Delay(600); break;
            case "huge": ClipboardTestNative.Write(source.Handle, (13u, Encoding.Unicode.GetBytes(new string('x', 1048577) + '\0'))); break;
            case "unsupported": ClipboardTestNative.Write(source.Handle, (ClipboardTestNative.Format("DailyToolkit.ClipRecall.Test.Unsupported"), [1, 2])); break;
            case "forward":
                ArgumentNullException.ThrowIfNull(path);
                ClipboardTestNative.Write(source.Handle, (13u, Encoding.Unicode.GetBytes("native external fixture\0")),
                    (ClipboardTestNative.Format("DailyToolkit.ClipRecall.Restore.v1"), await File.ReadAllBytesAsync(path))); break;
            default: throw new InvalidOperationException("Unknown native writer mode");
        }
    }
    private static Process StartClipboardWriter(string mode, string? path = null)
    {
        var start = new ProcessStartInfo(System.Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--clipboard-native-writer", ClipboardTestStation.Name, ClipboardTestStation.Desktop, mode }) start.ArgumentList.Add(arg);
        if (path is not null) start.ArgumentList.Add(path);
        return Process.Start(start) ?? throw new InvalidOperationException("Native writer process failed to start");
    }
    private static async Task WaitClipboardWriterAsync(Process writer)
    {
        var output = writer.StandardOutput.ReadToEndAsync(); var error = writer.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); await writer.WaitForExitAsync(timeout.Token);
        Require(writer.ExitCode == 0, "Isolated native writer failed: " + await output + await error);
    }
    private static async Task WriteClipboardFixtureAsync(string mode, string? path = null)
    { using var writer = StartClipboardWriter(mode, path); await WaitClipboardWriterAsync(writer); }
    private static async Task<ClipboardReadResult> ReadClipboardFixtureAsync(ClipboardNativeAccess native, ClipboardHistorySettings settings)
    {
        for (var index = 0; index < 10; index++)
        { var read = native.Read(settings); if (read.Disposition != ClipboardReadDisposition.Busy) return read; await Task.Delay(20); }
        throw new InvalidOperationException("Isolated clipboard remained busy");
    }
    private static async Task CheckClipboardNativeAsync()
    {
        var directory = Directory.CreateTempSubdirectory("DailyToolkit-clipboard-native-");
        await using var repository = new ClipboardHistoryRepository(directory.FullName);
        using var source = ClipboardTestWindow(); using var monitor = new ClipboardMonitor(source);
        using var probeWindow = ClipboardTestWindow();
        var identity = new ClipboardWriteIdentity(); var native = new ClipboardNativeAccess(probeWindow.Handle, identity);
        await using var processor = new ClipboardCaptureProcessor(repository, new ClipboardNativeAccess(source.Handle, identity));
        try
        {
            await repository.InitializeAsync(); var settings = new ClipboardHistorySettings { IsEnabled = true, ConsentAccepted = true, IncludeRestoredInWindowsHistory = true };
            processor.Configure(settings); monitor.Updated += processor.Notify; monitor.Configure(true);
            var notifications = 0; monitor.Updated += () => notifications++;
            await WriteClipboardFixtureAsync("text"); await WaitClipboardAsync(async () => (await repository.QueryAsync(new())).Total == 1);
            var text = (await repository.GetAsync((await repository.QueryAsync(new())).Records.Single().Id))!;
            await WriteClipboardFixtureAsync("text"); await WaitClipboardAsync(async () => (await repository.GetAsync(text.Id))!.LastCapturedAt > text.LastCapturedAt);
            await WriteClipboardFixtureAsync("link"); await WriteClipboardFixtureAsync("files");
            await WaitClipboardAsync(async () => (await repository.QueryAsync(new())).Total == 3);
            var files = (await repository.GetAsync((await repository.QueryAsync(new(Type: ClipboardContentType.Files))).Records.Single().Id))!;
            var order = (await repository.QueryAsync(new())).Records.Select(entry => entry.Id).ToArray();
            var captureTime = (await repository.GetAsync(text.Id))!.LastCapturedAt;
            for (var index = 0; index < 40; index++)
            {
                Require((await processor.RestoreAsync(index % 2 == 0 ? text : files)).Success, "Native restore failed");
                var read = await ReadClipboardFixtureAsync(native, settings);
                var marker = ClipboardTestNative.Bytes(ClipboardTestNative.Format("DailyToolkit.ClipRecall.Restore.v1"));
                var matchOffsets = Enumerable.Range(0, 8).Where(offset => identity.IsOwn(read.Sequence - (uint)offset, marker, index % 2 == 0 ? text.Content : files.Content));
                Require(read.Disposition == ClipboardReadDisposition.OwnWrite, $"Native own-write mismatch at {index}, disposition={read.Disposition}, sequence={read.Sequence}, matchingOffsets={string.Join(',', matchOffsets)}");
            }
            await Task.Delay(150);
            Console.WriteLine($"INFO native restore disposition={native.Read(settings).Disposition}, sequence={ClipboardNativeAccess.Sequence}, total={(await repository.QueryAsync(new())).Total}, order={(await repository.QueryAsync(new())).Records.Select(entry => entry.Id).SequenceEqual(order)}, captureTimeUnchanged={(await repository.GetAsync(text.Id))!.LastCapturedAt == captureTime}");
            Require(native.Read(settings).Disposition == ClipboardReadDisposition.OwnWrite && (await repository.QueryAsync(new())).Total == 3 &&
                (await repository.QueryAsync(new())).Records.Select(entry => entry.Id).SequenceEqual(order) && (await repository.GetAsync(text.Id))!.LastCapturedAt == captureTime,
                "Native own-write identity/sequence changed history");
            Require(BitConverter.ToUInt32(ClipboardTestNative.Bytes(ClipboardTestNative.Format("CanIncludeInClipboardHistory"))) == 1 &&
                BitConverter.ToUInt32(ClipboardTestNative.Bytes(ClipboardTestNative.Format("CanUploadToCloudClipboard"))) == 0, "Official clipboard history/cloud flags missing");
            settings = settings with { IncludeRestoredInWindowsHistory = false }; processor.Configure(settings);
            Require((await processor.RestoreAsync(text)).Success, "Default privacy restore failed"); await Task.Delay(100);
            Require(BitConverter.ToUInt32(ClipboardTestNative.Bytes(ClipboardTestNative.Format("CanIncludeInClipboardHistory"))) == 0 && (await repository.QueryAsync(new())).Total == 3,
                "Default Windows history exclusion failed");
            Console.WriteLine("PASS Isolated native WM_CLIPBOARDUPDATE, cross-process text/link/CF_HDROP, repeated external dedup, 40 restores and official history/cloud flags");

            var markerPath = Path.Combine(directory.FullName, "marker.bin");
            await File.WriteAllBytesAsync(markerPath, ClipboardTestNative.Bytes(ClipboardTestNative.Format("DailyToolkit.ClipRecall.Restore.v1")));
            await WriteClipboardFixtureAsync("forward", markerPath);
            await WaitClipboardAsync(async () => (await repository.GetAsync(text.Id))!.LastCapturedAt > captureTime);
            Require(native.Read(settings).Disposition == ClipboardReadDisposition.Captured, "Forwarded own marker on new sequence was suppressed");
            foreach (var mode in new[] { "exclude", "forbid", "unsupported", "huge" })
            {
                await WriteClipboardFixtureAsync(mode); await Task.Delay(100);
                var expected = mode switch { "unsupported" => ClipboardReadDisposition.Unsupported, "huge" => ClipboardReadDisposition.TooLarge, _ => ClipboardReadDisposition.Excluded };
                Require(native.Read(settings).Disposition == expected && (await repository.QueryAsync(new())).Total == 3, "Native privacy/unsupported/size handling failed: " + mode);
            }
            using (var ownerWriter = StartClipboardWriter("cloud"))
            {
                await WaitClipboardAsync(() => Task.FromResult(native.Read(settings with { ExcludedApplications = ["DailyToolkit.Desktop.Tests.exe"] }).Disposition == ClipboardReadDisposition.Excluded));
                await WaitClipboardWriterAsync(ownerWriter);
            }
            await WaitClipboardAsync(async () => (await repository.QueryAsync(new())).Total == 4);
            Console.WriteLine("PASS Isolated native forwarded marker, explicit source exclusions, history prohibition, cloud-only flag, unsupported formats and oversized data");

            var ready = Path.Combine(directory.FullName, "lock.ready"); using var locker = StartClipboardWriter("lock", ready);
            await WaitClipboardAsync(() => Task.FromResult(File.Exists(ready)));
            Require(!(await processor.RestoreAsync(text)).Success, "Native busy clipboard pretended successful restore");
            await WaitClipboardWriterAsync(locker); Require((await processor.RestoreAsync(files)).Success, "Native busy clipboard did not recover");
            Require((await ReadClipboardFixtureAsync(native, settings)).Disposition == ClipboardReadDisposition.Excluded, "Native default history marker missing after contention recovery");
            monitor.Configure(false); processor.Configure(settings with { IsPaused = true }); await processor.ReleaseIdleWorkerAsync();
            var previousNotifications = notifications; await WriteClipboardFixtureAsync("text"); await Task.Delay(150);
            Require(notifications == previousNotifications && !processor.HasWorker, "Paused native listener/STA worker retained activity");
            Console.WriteLine("PASS Isolated native cross-process clipboard contention, bounded failure/recovery, listener removal and STA teardown; interactive clipboard untouched");
        }
        finally
        { monitor.Configure(false); await processor.DisposeAsync(); await repository.DisposeAsync(); DeleteClipboardFixture(directory); }
    }

    private static async Task CheckClipboardNativeShortcutsAsync()
    {
        using var source = ClipboardTestWindow();
        var lens = new LensShortcutController(source);
        var clipboard = new LensShortcutController(source, hotkeyId: ClipboardHistoryController.HotkeyId);
        try
        {
            var lensChord = new KeyboardShortcut(3, 0x85, "Ctrl + Alt + F22");
            // Global registration requires WinSta0 (1459 in a private station).
            // The separate shortcut-only test never starts a clipboard monitor,
            // uses rare chords, and never sends keyboard input.
            var clipboardChord = new KeyboardShortcut(3, 0x86, "Ctrl + Alt + F23");
            var lensRegistered = lens.Configure(lensChord, LensActivationMode.Toggle);
            var lensError = Marshal.GetLastWin32Error();
            var clipboardRegistered = clipboard.Configure(clipboardChord, LensActivationMode.Toggle);
            var clipboardError = Marshal.GetLastWin32Error();
            Require(lensRegistered && clipboardRegistered,
                $"Shared native shortcut IDs could not coexist: lens={lensRegistered}/{lensError}, clipboard={clipboardRegistered}/{clipboardError}");
            var lensRequests = 0; var clipboardRequests = 0;
            lens.ToggleRequested += () => lensRequests++;
            clipboard.ToggleRequested += () => clipboardRequests++;
            // Dispatch native hotkey messages to this message-only HWND, without key
            // simulation or activating another application.
            Require(PostClipboardTestHotkey(source.Handle, 0x312, new IntPtr(LensShortcutController.HotkeyId),
                new IntPtr((int)(lensChord.VirtualKey << 16 | lensChord.Modifiers))), "Lens fixture message failed");
            Require(PostClipboardTestHotkey(source.Handle, 0x312, new IntPtr(ClipboardHistoryController.HotkeyId),
                new IntPtr((int)(clipboardChord.VirtualKey << 16 | clipboardChord.Modifiers))), "Clipboard fixture message failed");
            await WaitClipboardAsync(() => Task.FromResult(lensRequests == 1 && clipboardRequests == 1));
            using var competingWindow = ClipboardTestWindow();
            using var competing = new LensShortcutController(competingWindow, hotkeyId: 0xB032);
            Require(!competing.Configure(clipboardChord, LensActivationMode.Toggle), "Native shortcut conflict was not reported");
            Require(clipboard.Suspend(true) && !clipboard.IsRegistered &&
                competing.Configure(clipboardChord, LensActivationMode.Toggle), "Suspension did not release clipboard shortcut");
            Require(!clipboard.Suspend(false), "Occupied shortcut resume pretended success");
            competing.Dispose();
            Require(clipboard.Suspend(false) && clipboard.IsRegistered && lens.IsRegistered, "Shortcut resume disturbed existing lens shortcut");
            clipboard.Dispose();
            Require(competingWindow.Handle != IntPtr.Zero && lens.IsRegistered &&
                !clipboard.IsRegistered, "Clipboard disposal disturbed lens registration");
            using var replacement = new LensShortcutController(competingWindow, hotkeyId: 0xB033);
            Require(replacement.Configure(clipboardChord, LensActivationMode.Toggle), "Disposed clipboard shortcut remained registered");
            Console.WriteLine("PASS Native shared lens/clipboard hotkey IDs, independent message-only dispatch, conflict reporting, suspension/resume and disposal; no input simulation or clipboard access");
        }
        finally { clipboard.Dispose(); lens.Dispose(); }
    }
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool PostClipboardTestHotkey(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
