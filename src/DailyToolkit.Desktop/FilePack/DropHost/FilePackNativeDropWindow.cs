using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace DailyToolkit.Desktop.FilePack.DropHost;

// This branch does not initialize WPF. The helper owns the HWND, OLE apartment and GDI objects.
internal sealed class FilePackNativeDropWindow : IDisposable
{
    private const uint PaletteMessage = 0x8001;
    private const uint CloseMessage = 0x0010;
    private readonly uint _threadId = Native.GetCurrentThreadId();
    private readonly string _className = "DailyToolkit.FilePack.Drop." + Guid.NewGuid().ToString("N");
    private readonly Native.WindowProcedure _windowProcedure;
    private readonly DropTarget _dropTarget;
    private readonly nint _instance;
    private nint _handle, _dropTargetInterface, _backgroundBrush, _borderBrush, _font;
    private ushort _classAtom;
    private uint _fontDpi;
    private Palette? _pendingPalette, _palette;
    private Exception? _nativeFailure;
    private bool _oleInitialized, _registered, _closed, _running, _disposed;

    internal FilePackNativeDropWindow(Func<string[], bool> accept)
    {
        ArgumentNullException.ThrowIfNull(accept);
        _windowProcedure = WindowProcedure;
        _dropTarget = new(accept);
        _instance = Native.GetModuleHandle(null);
        if (_instance == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法取得拖放入口模块。");
        try
        {
            Marshal.ThrowExceptionForHR(Native.OleInitialize(0));
            _oleInitialized = true;
            var definition = new Native.WindowClass
            {
                Size = (uint)Marshal.SizeOf<Native.WindowClass>(),
                Procedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                Instance = _instance,
                Cursor = Native.LoadCursor(0, (nint)32512),
                ClassName = _className
            };
            if (definition.Cursor == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法取得拖放入口鼠标指针。");
            _classAtom = Native.RegisterClassEx(ref definition);
            if (_classAtom == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口窗口类未能注册。");
            // Hidden until the parent embeds it in its own reserved layout area.
            _handle = Native.CreateWindowEx(0x08000080, _className, "拖入文件或文件夹", 0x80000000,
                0, 0, 1, 1, 0, 0, _instance, 0);
            if (_handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口窗口未能创建。");
            if (_nativeFailure is not null) throw new InvalidOperationException("拖放入口初始化失败。", _nativeFailure);
            _dropTargetInterface = Marshal.GetComInterfaceForObject<DropTarget, INativeDropTarget>(_dropTarget);
            Marshal.ThrowExceptionForHR(Native.RegisterDragDrop(_handle, _dropTargetInterface));
            _registered = true;
        }
        catch (Exception creationFailure)
        {
            try { Dispose(); }
            catch (Exception cleanupFailure) { throw new AggregateException(creationFailure, cleanupFailure); }
            throw;
        }
    }

    internal nint Handle => Volatile.Read(ref _handle);

    internal void Run()
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) throw new InvalidOperationException("拖放入口消息循环已启动。");
        if (_closed)
        {
            if (_nativeFailure is not null) throw new InvalidOperationException("拖放入口运行失败。", _nativeFailure);
            return;
        }
        _running = true;
        try
        {
            while (true)
            {
                var result = Native.GetMessage(out var message, 0, 0, 0);
                if (result == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口消息读取失败。");
                if (result == 0) break;
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
            if (_nativeFailure is not null) throw new InvalidOperationException("拖放入口运行失败。", _nativeFailure);
        }
        finally { _running = false; }
    }

    internal void RequestStop() => Post(CloseMessage);

    // RGB values use the same 0xRRGGBB representation as the application's palette.
    internal void SetPalette(uint background, uint foreground, uint border)
    {
        if ((background | foreground | border) > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(background), "颜色应为 RGB 值。");
        if (Volatile.Read(ref _disposed)) return;
        Interlocked.Exchange(ref _pendingPalette, new(background, foreground, border));
        Post(PaletteMessage);
    }

    private void Post(uint message)
    {
        var handle = Handle;
        if (handle == 0 || Volatile.Read(ref _disposed)) return;
        if (!Native.PostMessage(handle, message, 0, 0) && Native.IsWindow(handle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口消息未能发送。");
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case PaletteMessage:
                    ApplyPendingPalette();
                    Native.InvalidateRect(hwnd, 0, false);
                    return 0;
                case 0x0018: // WM_SHOWWINDOW
                    ApplyPendingPalette();
                    if (wParam != 0 && _palette is null) Native.ShowWindow(hwnd, 0);
                    break;
                case 0x0005: // WM_SIZE
                    Native.InvalidateRect(hwnd, 0, false);
                    return 0;
                case 0x02E0: // WM_DPICHANGED
                case 0x02E3: // WM_DPICHANGED_AFTERPARENT
                    _fontDpi = 0;
                    Native.InvalidateRect(hwnd, 0, false);
                    return 0;
                case 0x000F: // WM_PAINT
                    Paint(hwnd);
                    return 0;
                case 0x0014: // WM_ERASEBKGND; WM_PAINT fills the complete client area.
                    return 1;
                case 0x0021: // WM_MOUSEACTIVATE
                    return 3; // MA_NOACTIVATE: dropping does not steal keyboard focus.
                case CloseMessage:
                    RevokeDropTarget(hwnd);
                    if (!Native.DestroyWindow(hwnd) && Native.IsWindow(hwnd))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口窗口未能关闭。");
                    return 0;
                case 0x0002: // WM_DESTROY
                    RevokeDropTarget(hwnd);
                    _closed = true;
                    if (_running) Native.PostQuitMessage(0);
                    return 0;
                case 0x0082: // WM_NCDESTROY
                    Interlocked.CompareExchange(ref _handle, 0, hwnd);
                    break;
            }
            return Native.DefWindowProc(hwnd, message, wParam, lParam);
        }
        catch (Exception exception)
        {
            // A managed exception must never escape a Win32 callback.
            _nativeFailure ??= exception;
            _closed = true;
            if (_running) Native.PostQuitMessage(1);
            return 0;
        }
    }

    private void ApplyPendingPalette()
    {
        var next = Interlocked.Exchange(ref _pendingPalette, null);
        if (next is null || next == _palette) return;
        var background = Native.CreateSolidBrush(ToColorRef(next.Background));
        if (background == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口背景画刷未能创建。");
        var border = Native.CreateSolidBrush(ToColorRef(next.Border));
        if (border == 0)
        {
            var error = Marshal.GetLastWin32Error();
            Native.DeleteObject(background);
            throw new Win32Exception(error, "拖放入口边框画刷未能创建。");
        }
        var oldBackground = _backgroundBrush;
        var oldBorder = _borderBrush;
        _backgroundBrush = background;
        _borderBrush = border;
        _palette = next;
        try { DeleteGdiObject(oldBackground); }
        finally { DeleteGdiObject(oldBorder); }
    }

    private void Paint(nint hwnd)
    {
        var dc = Native.BeginPaint(hwnd, out var paint);
        if (dc == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口画布未能创建。");
        try
        {
            ApplyPendingPalette();
            if (_palette is null) return;
            if (!Native.GetClientRect(hwnd, out var rectangle))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口尺寸读取失败。");
            if (rectangle.Right <= rectangle.Left || rectangle.Bottom <= rectangle.Top) return;
            if (Native.FillRect(dc, ref rectangle, _backgroundBrush) == 0 || Native.FrameRect(dc, ref rectangle, _borderBrush) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口背景绘制失败。");
            var dpi = Native.GetDpiForWindow(hwnd);
            if (dpi == 0) throw new InvalidOperationException("拖放入口 DPI 读取失败。");
            EnsureFont(dpi);
            var previous = Native.SelectObject(dc, _font);
            if (previous is 0 or -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口字体选取失败。");
            try
            {
                if (Native.SetBkMode(dc, 1) == 0 || Native.SetTextColor(dc, ToColorRef(_palette.Foreground)) == 0xFFFFFFFF)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口文字颜色设置失败。");
                var padding = Math.Max(1, (int)Math.Round(8d * dpi / 96d));
                rectangle.Left += padding;
                rectangle.Right -= padding;
                if (rectangle.Right > rectangle.Left && Native.DrawText(dc, "拖入文件或文件夹", -1, ref rectangle, 0x00008825) == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口文字绘制失败。");
            }
            finally { Native.SelectObject(dc, previous); }
        }
        finally
        {
            if (!Native.EndPaint(hwnd, ref paint))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口绘制未能结束。");
        }
    }

    private void EnsureFont(uint dpi)
    {
        if (_font != 0 && _fontDpi == dpi) return;
        var height = -(int)Math.Max(1, Math.Round(14d * dpi / 96d));
        var font = Native.CreateFont(height, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Microsoft YaHei UI");
        if (font == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口字体未能创建。");
        var previous = _font;
        _font = font;
        _fontDpi = dpi;
        DeleteGdiObject(previous);
    }

    public void Dispose()
    {
        VerifyThread();
        if (_disposed) return;
        _disposed = true;
        List<Exception>? failures = null;
        void Cleanup(Action action)
        {
            try { action(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }
        var handle = Handle;
        if (_registered)
        {
            Cleanup(() => RevokeDropTarget(handle));
        }
        if (_dropTargetInterface != 0)
        {
            Marshal.Release(_dropTargetInterface);
            _dropTargetInterface = 0;
        }
        if (handle != 0 && Native.IsWindow(handle))
            Cleanup(() =>
            {
                if (!Native.DestroyWindow(handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口窗口未能释放。");
            });
        Interlocked.Exchange(ref _handle, 0);
        _closed = true;
        Cleanup(() => DeleteGdiObject(_font));
        Cleanup(() => DeleteGdiObject(_backgroundBrush));
        Cleanup(() => DeleteGdiObject(_borderBrush));
        _font = _backgroundBrush = _borderBrush = 0;
        if (_classAtom != 0)
        {
            _classAtom = 0;
            Cleanup(() =>
            {
                if (!Native.UnregisterClass(_className, _instance))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口窗口类未能释放。");
            });
        }
        if (_oleInitialized)
        {
            _oleInitialized = false;
            Native.OleUninitialize();
        }
        Interlocked.Exchange(ref _pendingPalette, null);
        GC.KeepAlive(_dropTarget);
        GC.KeepAlive(_windowProcedure);
        if (failures is { Count: > 0 }) throw new AggregateException("拖放入口资源释放失败。", failures);
    }

    private void VerifyThread()
    {
        if (Native.GetCurrentThreadId() != _threadId)
            throw new InvalidOperationException("拖放入口应在创建线程运行和释放。");
    }

    private void RevokeDropTarget(nint handle)
    {
        if (!_registered) return;
        Marshal.ThrowExceptionForHR(Native.RevokeDragDrop(handle));
        _registered = false;
    }

    private static uint ToColorRef(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
    private static void DeleteGdiObject(nint value)
    {
        if (value != 0 && !Native.DeleteObject(value))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "拖放入口绘图资源未能释放。");
    }

    private sealed record Palette(uint Background, uint Foreground, uint Border);

    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface INativeDropTarget
    {
        [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, Native.Point point, ref uint effect);
        [PreserveSig] int DragOver(uint keys, Native.Point point, ref uint effect);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] IDataObject data, uint keys, Native.Point point, ref uint effect);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DropTarget(Func<string[], bool> accept) : INativeDropTarget
    {
        private const uint Copy = 1;
        private const int MaximumPaths = 100, MaximumPathLength = 32767, MaximumPathBytes = 128 * 1024;
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private bool _supportsFiles;

        public int DragEnter(IDataObject data, uint keys, Native.Point point, ref uint effect)
        {
            try
            {
                var format = FileDropFormat();
                _supportsFiles = (effect & Copy) != 0 && data.QueryGetData(ref format) == 0;
                effect = _supportsFiles ? Copy : 0;
            }
            catch { _supportsFiles = false; effect = 0; }
            return 0;
        }

        public int DragOver(uint keys, Native.Point point, ref uint effect)
        {
            effect = _supportsFiles && (effect & Copy) != 0 ? Copy : 0;
            return 0;
        }

        public int DragLeave() { _supportsFiles = false; return 0; }

        public int Drop(IDataObject data, uint keys, Native.Point point, ref uint effect)
        {
            var allowsCopy = (effect & Copy) != 0;
            effect = 0;
            _supportsFiles = false;
            if (!allowsCopy) return 0;
            var medium = default(STGMEDIUM);
            var acquired = false;
            try
            {
                var format = FileDropFormat();
                if (data.QueryGetData(ref format) != 0) return 0;
                data.GetData(ref format, out medium);
                acquired = true;
                if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == 0) return 0;
                var paths = ReadPaths(medium.unionmember);
                if (paths is { Length: > 0 } && accept(paths)) effect = Copy;
            }
            catch { effect = 0; }
            finally
            {
                if (acquired)
                {
                    try { Native.ReleaseStgMedium(ref medium); }
                    catch { effect = 0; }
                }
            }
            return 0;
        }

        private static FORMATETC FileDropFormat() => new()
        {
            cfFormat = 15, ptd = 0, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL
        };

        private static string[]? ReadPaths(nint drop)
        {
            var count = Native.DragQueryFile(drop, uint.MaxValue, null, 0);
            if (count is 0 or > MaximumPaths) return null;
            List<string> paths = new((int)count);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            var bytes = 0;
            for (uint index = 0; index < count; index++)
            {
                var length = Native.DragQueryFile(drop, index, null, 0);
                if (length is 0 or > MaximumPathLength) return null;
                var buffer = new StringBuilder((int)length + 1);
                if (Native.DragQueryFile(drop, index, buffer, length + 1) != length || buffer.Length != length) return null;
                var path = buffer.ToString();
                if (path.IndexOf('\0') >= 0 || !Path.IsPathFullyQualified(path)) return null;
                path = Path.GetFullPath(path);
                if (path.Length > MaximumPathLength) return null;
                bytes = checked(bytes + Utf8.GetByteCount(path));
                if (bytes > MaximumPathBytes) return null;
                if (seen.Add(path)) paths.Add(path);
            }
            return paths.ToArray();
        }
    }

    private static class Native
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point { internal int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rectangle { internal int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Message
        {
            internal nint Hwnd;
            internal uint Id;
            internal nuint WParam;
            internal nint LParam;
            internal uint Time;
            internal Point Point;
            internal uint Private;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal unsafe struct Paint
        {
            internal nint Dc;
            internal int Erase;
            internal Rectangle Rectangle;
            internal int Restore, IncUpdate;
            internal fixed byte Reserved[32];
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClass
        {
            internal uint Size, Style;
            internal nint Procedure;
            internal int ClassExtra, WindowExtra;
            internal nint Instance, Icon, Cursor, Background;
            [MarshalAs(UnmanagedType.LPWStr)] internal string? MenuName;
            [MarshalAs(UnmanagedType.LPWStr)] internal string ClassName;
            internal nint SmallIcon;
        }

        [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint GetModuleHandle(string? name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint LoadCursor(nint instance, nint name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern ushort RegisterClassEx(ref WindowClass definition);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterClass(string name, nint instance);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] internal static extern void PostQuitMessage(int code);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int GetMessage(out Message message, nint hwnd, uint first, uint last);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint DispatchMessage(ref Message message);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint hwnd, int command);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InvalidateRect(nint hwnd, nint rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint BeginPaint(nint hwnd, out Paint paint);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EndPaint(nint hwnd, ref Paint paint);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint hwnd, out Rectangle rectangle);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
        [DllImport("user32.dll", SetLastError = true)] internal static extern int FillRect(nint dc, ref Rectangle rectangle, nint brush);
        [DllImport("user32.dll", SetLastError = true)] internal static extern int FrameRect(nint dc, ref Rectangle rectangle, nint brush);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern int DrawText(nint dc, string text, int length, ref Rectangle rectangle, uint format);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateSolidBrush(uint color);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint characterSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint SelectObject(nint dc, nint value);
        [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern int SetBkMode(nint dc, int mode);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern uint SetTextColor(nint dc, uint color);
        [DllImport("ole32.dll")] internal static extern int OleInitialize(nint reserved);
        [DllImport("ole32.dll")] internal static extern void OleUninitialize();
        [DllImport("ole32.dll")] internal static extern int RegisterDragDrop(nint hwnd, nint target);
        [DllImport("ole32.dll")] internal static extern int RevokeDragDrop(nint hwnd);
        [DllImport("ole32.dll")] internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] internal static extern uint DragQueryFile(nint drop, uint index, StringBuilder? path, uint capacity);
    }
}
