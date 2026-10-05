using System.ComponentModel;
using System.Runtime.InteropServices;
using DailyUSE.Core.Gaming;

namespace DailyUSE.Desktop.Gaming;

public sealed record CaptureMonitor(IntPtr Handle, PixelBounds Bounds, string Name)
{
    public override string ToString() => Name;
}

internal sealed class LensNativeWindow : IDisposable
{
    private const string ClassName = "DailyUSE.LocalLens";
    private static readonly WindowProcedure Procedure = WindowProc;
    private static bool _registered;
    public IntPtr Handle { get; private set; }

    public LensNativeWindow(PixelBounds bounds)
    {
        if (!_registered)
        {
            var type = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = Procedure,
                Instance = GetModuleHandle(null), ClassName = ClassName };
            if (RegisterClassEx(ref type) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
        }
        // Layered + transparent forwards mouse input to the underlying game; no activation or taskbar entry.
        Handle = CreateWindowEx(0x080800A8, ClassName, "DailyUSE 局部放大", 0x80000000,
            bounds.Left, bounds.Top, bounds.Width, bounds.Height, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (Handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!SetLayeredWindowAttributes(Handle, 0, 255, 2) || !SetWindowDisplayAffinity(Handle, 0x11))
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error());
            Dispose();
            throw error;
        }
    }

    public void Show() => ShowWindow(Handle, 4);
    public void Hide() => ShowWindow(Handle, 0);
    public void Move(PixelBounds bounds) => SetWindowPos(Handle, new IntPtr(-1), bounds.Left, bounds.Top,
        bounds.Width, bounds.Height, 0x0010 | 0x0200 | 0x4000);
    public void Dispose() { if (Handle != IntPtr.Zero) { DestroyWindow(Handle); Handle = IntPtr.Zero; } }

    public static IReadOnlyList<CaptureMonitor> Monitors()
    {
        var monitors = new List<(IntPtr Handle, MonitorInfo Info)>();
        MonitorProcedure callback = (IntPtr monitor, IntPtr _, ref NativeRect bounds, IntPtr data) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info)) monitors.Add((monitor, info));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return monitors.OrderByDescending(m => (m.Info.Flags & 1) != 0).Select((m, i) => new CaptureMonitor(m.Handle,
            new(m.Info.Monitor.Left, m.Info.Monitor.Top, m.Info.Monitor.Right-m.Info.Monitor.Left, m.Info.Monitor.Bottom-m.Info.Monitor.Top),
            $"显示器 {i+1}{((m.Info.Flags & 1) != 0 ? "（主）" : "")} · {m.Info.Monitor.Right-m.Info.Monitor.Left} × {m.Info.Monitor.Bottom-m.Info.Monitor.Top}")).ToArray();
    }

    public static (int X, int Y) Cursor()
    {
        GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    private static IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam) => message switch
    {
        0x0084 => new IntPtr(-1), // HTTRANSPARENT
        0x0021 => new IntPtr(3), // MA_NOACTIVATE
        _ => DefWindowProc(window, message, wParam, lParam)
    };
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProcedure(IntPtr h, uint m, IntPtr w, IntPtr l);
    private delegate bool MonitorProcedure(IntPtr h, IntPtr dc, ref NativeRect bounds, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public WindowProcedure? Procedure; public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background; public string? MenuName, ClassName; public IntPtr SmallIcon;
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern ushort RegisterClassEx(ref WindowClass type);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern IntPtr CreateWindowEx(uint ex, string type, string title, uint style, int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h,int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetLayeredWindowAttributes(IntPtr h,uint key,byte alpha,uint flags);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowDisplayAffinity(IntPtr h,uint affinity);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc,IntPtr clip,MonitorProcedure callback,IntPtr data);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr h,ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
}
