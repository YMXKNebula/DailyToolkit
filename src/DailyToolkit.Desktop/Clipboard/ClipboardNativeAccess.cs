using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DailyToolkit.Core.Clipboard;

namespace DailyToolkit.Desktop.ClipRecall;

internal enum ClipboardReadDisposition { Captured, OwnWrite, Empty, Unsupported, Excluded, TooLarge, Busy, Failed }
internal sealed record ClipboardReadResult(ClipboardReadDisposition Disposition, uint Sequence,
    ClipboardContent? Content = null, string? Source = null, string Message = "", byte[]? Marker = null);
internal sealed record ClipboardRestoreAttempt(ClipboardOperationResult Result, ClipboardReadResult? Previous = null);
internal interface IClipboardAccess
{
    ClipboardReadResult Read(ClipboardHistorySettings settings);
    ClipboardRestoreAttempt Restore(ClipboardContent content, ClipboardHistorySettings settings);
}

// Identity is attached to the actual clipboard payload, not to a time window.
// An application forwarding the marker on a NEW clipboard sequence is captured.
internal sealed class ClipboardWriteIdentity
{
    private readonly Guid _session = Guid.NewGuid();
    private readonly ConcurrentDictionary<uint, (Guid Operation, string Hash)> _writes = new();
    private readonly ConcurrentQueue<uint> _order = new();
    internal byte[] CreateMarker(Guid operation)
    {
        var bytes = new byte[32]; _session.TryWriteBytes(bytes); operation.TryWriteBytes(bytes.AsSpan(16)); return bytes;
    }
    internal void Register(uint sequence, Guid operation, ClipboardContent content)
    {
        _writes[sequence] = (operation, content.StableHash()); _order.Enqueue(sequence);
        while (_order.Count > 128 && _order.TryDequeue(out var old)) _writes.TryRemove(old, out _);
    }
    internal bool IsOwn(uint sequence, byte[]? marker, ClipboardContent content) => marker is { Length: 32 } &&
        new Guid(marker.AsSpan(0, 16)) == _session && _writes.TryGetValue(sequence, out var write) &&
        new Guid(marker.AsSpan(16, 16)) == write.Operation && write.Hash == content.StableHash();
}

internal sealed class ClipboardNativeAccess : IClipboardAccess
{
    private const uint UnicodeText = 13, FileDrop = 15, Dib = 8, DibV5 = 17, Bitmap = 2;
    private readonly IntPtr _owner;
    private readonly ClipboardWriteIdentity _identity;
    private readonly uint _marker, _exclude, _history, _cloud, _png;
    public ClipboardNativeAccess(IntPtr owner, ClipboardWriteIdentity? identity = null)
    {
        _owner = owner; _identity = identity ?? new();
        _marker = Format("DailyToolkit.ClipRecall.Restore.v1");
        _exclude = Format("ExcludeClipboardContentFromMonitorProcessing");
        _history = Format("CanIncludeInClipboardHistory");
        _cloud = Format("CanUploadToCloudClipboard");
        _png = Format("PNG");
    }
    private static uint Format(string name)
    {
        var id = RegisterClipboardFormat(name);
        if (id == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册剪贴板格式。");
        return id;
    }
    internal static uint Sequence => GetClipboardSequenceNumber();
    public ClipboardReadResult Read(ClipboardHistorySettings settings)
    {
        if (!OpenClipboard(_owner)) return new(ClipboardReadDisposition.Busy, Sequence, Message: "剪贴板暂被其他程序占用。");
        ClipboardReadResult raw;
        try { raw = ReadOpened(settings); }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
        { raw = ClassifyReadError(exception, Sequence); }
        finally { CloseClipboard(); }
        return FinishRead(raw, settings);
    }
    private static ClipboardReadResult ClassifyReadError(Exception exception, uint sequence) => exception switch
    {
        ClipboardImageLimitException => new(ClipboardReadDisposition.TooLarge, sequence, Message: exception.Message),
        ClipboardImageFormatException => new(ClipboardReadDisposition.Unsupported, sequence, Message: exception.Message),
        _ => new(ClipboardReadDisposition.Failed, sequence, Message: "剪贴板格式读取失败，已安全忽略。")
    };
    private ClipboardReadResult FinishRead(ClipboardReadResult raw, ClipboardHistorySettings settings)
    {
        if (raw.Content?.Type != ClipboardContentType.Image) return raw;
        try
        {
            // The native lock only copies a bounded payload. Decode/normalize and
            // hashing happen after CloseClipboard, off UI on the STA worker.
            var content = raw.Content.BinaryFormat == "png" ? ClipboardImageCodec.FromPng(raw.Content.Binary!, settings.MaximumImageMegabytes) :
                ClipboardImageCodec.FromDib(raw.Content.Binary!, settings.MaximumImageMegabytes);
            return raw with { Content = content, Marker = null,
                Disposition = _identity.IsOwn(raw.Sequence, raw.Marker, content) ? ClipboardReadDisposition.OwnWrite : ClipboardReadDisposition.Captured };
        }
        catch (ClipboardImageLimitException exception)
        { return new(ClipboardReadDisposition.TooLarge, raw.Sequence, Message: exception.Message); }
        catch (ClipboardImageFormatException exception)
        { return new(ClipboardReadDisposition.Unsupported, raw.Sequence, Message: exception.Message); }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
        { return ClassifyReadError(exception, raw.Sequence); }
    }
    private ClipboardReadResult ReadOpened(ClipboardHistorySettings settings)
    {
        var sequence = Sequence;
        // Privacy flags are checked BEFORE rendering or reading the payload. A
        // malformed explicit history flag is treated as an exclusion, fail closed.
        if (IsClipboardFormatAvailable(_exclude) || IsClipboardFormatAvailable(_history) && ReadDword(_history) != 1)
            return new(ClipboardReadDisposition.Excluded, sequence, Message: "来源明确禁止历史保存，已忽略。");
        var source = SourceApplication();
        if (source is not null && settings.ExcludedApplications.Any(value =>
            string.Equals(value, source, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(value), source, StringComparison.OrdinalIgnoreCase)))
            return new(ClipboardReadDisposition.Excluded, sequence, Message: "已忽略排除应用的内容。");
        ClipboardContent content;
        var maximum = settings.MaximumTextKilobytes * 1024L;
        if (IsClipboardFormatAvailable(FileDrop))
        {
            var drop = GetClipboardData(FileDrop);
            if (drop == IntPtr.Zero) throw new Win32Exception();
            var count = DragQueryFile(drop, uint.MaxValue, null, 0);
            if (count == 0) return new(ClipboardReadDisposition.Empty, Sequence);
            if (count > 4096) return new(ClipboardReadDisposition.TooLarge, Sequence, Message: "文件列表过大，未记录。");
            var files = new List<string>(); long bytes = 0;
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFile(drop, index, null, 0);
                if (length > 32767) throw new InvalidDataException();
                var buffer = new StringBuilder(checked((int)length + 1));
                if (DragQueryFile(drop, index, buffer, (uint)buffer.Capacity) != length) throw new InvalidDataException();
                var path = buffer.ToString(); bytes += Encoding.UTF8.GetByteCount(path) + 1;
                if (bytes > maximum) return new(ClipboardReadDisposition.TooLarge, Sequence, Message: "文件列表超过单条大小限制，未记录。");
                files.Add(path);
            }
            content = ClipboardContent.FromFiles(files);
        }
        else if (IsClipboardFormatAvailable(_png) || IsClipboardFormatAvailable(DibV5) || IsClipboardFormatAvailable(Dib))
        {
            var format = IsClipboardFormatAvailable(_png) ? _png : IsClipboardFormatAvailable(DibV5) ? DibV5 : Dib;
            var limit = format == _png ? settings.MaximumImageMegabytes * 1024L * 1024 :
                ClipboardImageCodec.PixelLimit(settings.MaximumImageMegabytes) + 4096;
            var bytes = ReadImageBytes(format, limit);
            content = new(ClipboardContentType.Image, "", [], bytes, format == _png ? "png" : "dib");
        }
        else if (IsClipboardFormatAvailable(UnicodeText))
        {
            var handle = GetClipboardData(UnicodeText);
            if (handle == IntPtr.Zero) throw new Win32Exception();
            var size = GlobalSize(handle).ToUInt64();
            if (size == 0) return new(ClipboardReadDisposition.Empty, Sequence);
            if (size > (ulong)(maximum * 2 + 2)) return new(ClipboardReadDisposition.TooLarge, Sequence, Message: "文本超过单条大小限制，未记录。");
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) throw new Win32Exception();
            string text;
            try
            {
                text = Marshal.PtrToStringUni(pointer, checked((int)(size / 2))) ?? "";
                var terminator = text.IndexOf('\0'); if (terminator >= 0) text = text[..terminator];
            }
            finally { GlobalUnlock(handle); }
            if (text.Length == 0) return new(ClipboardReadDisposition.Empty, Sequence);
            if (Encoding.UTF8.GetByteCount(text) > maximum)
                return new(ClipboardReadDisposition.TooLarge, Sequence, Message: "文本超过单条大小限制，未记录。");
            content = ClipboardContent.FromText(text);
        }
        else return new(ClipboardReadDisposition.Unsupported, sequence, Message: "当前格式尚未支持，已安全忽略。");
        // Delayed rendering may change the sequence while the clipboard is locked.
        var marker = IsClipboardFormatAvailable(_marker) ? ReadBytes(_marker, 32) : null;
        sequence = Sequence;
        if (content.Type == ClipboardContentType.Image) return new(ClipboardReadDisposition.Captured, sequence, content, source, Marker: marker);
        return new(_identity.IsOwn(sequence, marker, content) ? ClipboardReadDisposition.OwnWrite : ClipboardReadDisposition.Captured,
            sequence, content, source);
    }
    private static string? SourceApplication()
    {
        var owner = GetClipboardOwner(); if (owner == IntPtr.Zero) return null;
        GetWindowThreadProcessId(owner, out var id); if (id == 0) return null;
        try { using var process = Process.GetProcessById(checked((int)id)); return process.ProcessName; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }
    private static byte[]? ReadBytes(uint format, int maximum)
    {
        var handle = GetClipboardData(format); if (handle == IntPtr.Zero) return null;
        var size = GlobalSize(handle).ToUInt64(); if (size == 0 || size > (ulong)maximum) return null;
        var pointer = GlobalLock(handle); if (pointer == IntPtr.Zero) return null;
        try { var bytes = new byte[(int)size]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
        finally { GlobalUnlock(handle); }
    }
    private static uint? ReadDword(uint format)
    { var bytes = ReadBytes(format, 4); return bytes is { Length: 4 } ? BitConverter.ToUInt32(bytes) : null; }
    private static byte[] ReadImageBytes(uint format, long maximum)
    {
        var handle = GetClipboardData(format); if (handle == IntPtr.Zero) throw new Win32Exception();
        var size = GlobalSize(handle).ToUInt64();
        if (size > (ulong)maximum) throw new ClipboardImageLimitException();
        if (size == 0) throw new ClipboardImageFormatException();
        var pointer = GlobalLock(handle); if (pointer == IntPtr.Zero) throw new Win32Exception();
        try { var bytes = new byte[checked((int)size)]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
        finally { GlobalUnlock(handle); }
    }

    public ClipboardRestoreAttempt Restore(ClipboardContent content, ClipboardHistorySettings settings)
    {
        var attempt = RestoreCore(content, settings);
        return attempt.Previous is { } previous ? attempt with { Previous = FinishRead(previous, settings) } : attempt;
    }
    private ClipboardRestoreAttempt RestoreCore(ClipboardContent content, ClipboardHistorySettings settings)
    {
        if (content.Type is not (ClipboardContentType.Text or ClipboardContentType.Link or ClipboardContentType.Files or ClipboardContentType.Image))
            return new(new(false, "当前内容类型尚不支持恢复。"));
        // Allocate before emptying. Once transferred, Windows owns each HGLOBAL.
        var operation = Guid.NewGuid();
        var buffers = new List<(uint Format, IntPtr Handle)>();
        try
        {
            buffers.Add((_marker, Allocate(_identity.CreateMarker(operation))));
            buffers.Add((_history, Allocate(BitConverter.GetBytes(settings.IncludeRestoredInWindowsHistory ? 1u : 0u))));
            buffers.Add((_cloud, Allocate(BitConverter.GetBytes(0u))));
            if (content.Type == ClipboardContentType.Image)
            {
                buffers.Add((_png, Allocate(content.Binary ?? throw new InvalidDataException())));
                buffers.Add((DibV5, Allocate(ClipboardImageCodec.Dib(content, true))));
                var dib = ClipboardImageCodec.Dib(content, false);
                buffers.Add((Dib, Allocate(dib)));
                // Supply bitmap companions before registering the final sequence,
                // preventing Windows' format synthesis from disguising our write.
                buffers.Add((Bitmap, AllocateBitmap(dib)));
            }
            else
                buffers.Add((content.Type == ClipboardContentType.Files ? FileDrop : UnicodeText,
                    Allocate(content.Type == ClipboardContentType.Files ? FileDropBytes(content.Paths) : Encoding.Unicode.GetBytes(content.Text + '\0'))));
            if (content.Type is ClipboardContentType.Text or ClipboardContentType.Link)
            {
                // Windows synthesizes CF_TEXT, CF_OEMTEXT and CF_LOCALE when
                // closing a Unicode-only clipboard, advancing the sequence AFTER
                // our ledger entry. Supply these standard companion formats now,
                // under the same lock, so the registered sequence is final. The
                // canonical payload remains the exact Unicode text.
                buffers.Add((1, Allocate(LegacyTextBytes(content.Text, 0))));
                buffers.Add((7, Allocate(LegacyTextBytes(content.Text, 1))));
                buffers.Add((16, Allocate(BitConverter.GetBytes(GetUserDefaultLCID()))));
            }
            if (!OpenClipboard(_owner)) return new(new(false, "剪贴板暂被其他程序占用，请重试。"));
            try
            {
                // Preflight observes a real external copy before THIS restore
                // replaces it, even if its WM_CLIPBOARDUPDATE is still queued.
                ClipboardReadResult previous;
                try { previous = ReadOpened(settings); }
                catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
                { previous = new(ClipboardReadDisposition.Failed, Sequence, Message: "恢复前的内容未能读取。"); }
                if (!EmptyClipboard()) return new(new(false, "无法清空剪贴板以恢复记录。"), previous);
                for (var index = 0; index < buffers.Count; index++)
                {
                    var buffer = buffers[index];
                    if (SetClipboardData(buffer.Format, buffer.Handle) == IntPtr.Zero)
                        return new(new(false, "恢复未完成，剪贴板格式写入失败；请重试。"), previous);
                    buffers[index] = (buffer.Format, IntPtr.Zero);
                }
                _identity.Register(Sequence, operation, content);
                return new(ClipboardOperationResult.Ok, previous);
            }
            finally { CloseClipboard(); }
        }
        catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception) || exception is OutOfMemoryException)
        { return new(new(false, "剪贴板恢复失败，未能分配或写入数据。")); }
        finally
        {
            foreach (var buffer in buffers) if (buffer.Handle != IntPtr.Zero)
            { if (buffer.Format == Bitmap) DeleteObject(buffer.Handle); else GlobalFree(buffer.Handle); }
        }
    }
    private static IntPtr AllocateBitmap(byte[] dib)
    {
        var bitmap = CreateDIBSection(IntPtr.Zero, dib, 0, out var bits, IntPtr.Zero, 0);
        if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) { if (bitmap != IntPtr.Zero) DeleteObject(bitmap); throw new Win32Exception(); }
        try { Marshal.Copy(dib, 40, bits, dib.Length - 40); return bitmap; }
        catch { DeleteObject(bitmap); throw; }
    }
    private static byte[] FileDropBytes(string[] paths)
    {
        var list = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
        var bytes = new byte[20 + list.Length]; BitConverter.GetBytes(20u).CopyTo(bytes, 0);
        BitConverter.GetBytes(1u).CopyTo(bytes, 16); list.CopyTo(bytes, 20); return bytes;
    }
    private static byte[] LegacyTextBytes(string text, uint codePage)
    {
        var terminated = text + '\0';
        var length = WideCharToMultiByte(codePage, 0, terminated, terminated.Length, null, 0, IntPtr.Zero, IntPtr.Zero);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var bytes = new byte[length];
        if (WideCharToMultiByte(codePage, 0, terminated, terminated.Length, bytes, length, IntPtr.Zero, IntPtr.Zero) != length)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return bytes;
    }
    private static IntPtr Allocate(byte[] bytes)
    {
        var handle = GlobalAlloc(0x0042, (nuint)bytes.Length); if (handle == IntPtr.Zero) throw new OutOfMemoryException();
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) { GlobalFree(handle); throw new Win32Exception(); }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
        finally { GlobalUnlock(handle); }
        return handle;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? path, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern uint GetUserDefaultLCID();
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, byte[] info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int WideCharToMultiByte(uint codePage, uint flags,
        string text, int characters, byte[]? output, int bytes, IntPtr defaultCharacter, IntPtr usedDefaultCharacter);
}
