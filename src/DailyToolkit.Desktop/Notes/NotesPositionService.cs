using System.ComponentModel;
using System.Runtime.InteropServices;
using DailyToolkit.Core.Notes;

namespace DailyToolkit.Desktop.Notes;

internal static class NotesPositionService
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo
    { public int Size; public Rect Monitor, Work; public uint Flags; }
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);

    public static IReadOnlyList<NotesRect> WorkAreas()
    {
        var areas = new List<(bool Primary, NotesRect Area)>();
        var success = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(monitor, ref info)) return false;
                areas.Add(((info.Flags & 1) != 0, new(info.Work.Left, info.Work.Top,
                    info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top))); return true;
            }, IntPtr.Zero);
        if (!success || areas.Count == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取当前显示器。");
        return areas.OrderByDescending(item => item.Primary).Select(item => item.Area).ToArray();
    }
    public static NotesRect Bounds(IntPtr handle)
    {
        if (!GetWindowRect(handle, out var rect)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取浮笺位置。");
        return new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }
    public static void Place(IntPtr handle, double? x, double? y)
    {
        var bounds = Bounds(handle); var areas = WorkAreas();
        var requested = bounds with { X = x ?? areas[0].X + 32, Y = y ?? areas[0].Y + 32 };
        var recovered = NotesPosition.Recover(requested, areas);
        Move(handle, recovered.X, recovered.Y);
    }
    public static void Recover(IntPtr handle)
    {
        var bounds = Bounds(handle); var recovered = NotesPosition.Recover(bounds, WorkAreas());
        if (bounds != recovered) Move(handle, recovered.X, recovered.Y);
    }
    private static void Move(IntPtr handle, double x, double y)
    {
        if (!SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0, 0x0015))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法调整浮笺位置。"); // NOACTIVATE | NOSIZE | NOZORDER
    }
    public static (int X, int Y) Cursor()
    {
        if (!GetCursorPos(out var point)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取拖动位置。");
        return (point.X, point.Y);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
