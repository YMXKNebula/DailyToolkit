using System.Runtime.InteropServices;

namespace DailyToolkit.Desktop.Notes;

internal interface INotesForeground
{
    IntPtr Current { get; }
    bool IsValid(IntPtr window);
    bool Activate(IntPtr window);
}

internal sealed class NotesForeground : INotesForeground
{
    public IntPtr Current => GetForegroundWindow();
    public bool IsValid(IntPtr window) => window != IntPtr.Zero && IsWindow(window) && IsWindowVisible(window) && IsWindowEnabled(window);
    public bool Activate(IntPtr window) => SetForegroundWindow(window);
    internal static void ReleaseKeyboard() { SetFocus(IntPtr.Zero); }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr window);
}

internal sealed class NotesFocusManager(INotesForeground foreground, Func<IntPtr> noteHandle)
{
    internal IntPtr Previous { get; private set; }
    public void Capture()
    {
        var current = foreground.Current;
        if (current == noteHandle()) return;
        Previous = foreground.IsValid(current) ? current : IntPtr.Zero;
    }
    public bool Restore() => Previous != noteHandle() && foreground.IsValid(Previous) && foreground.Activate(Previous);
}
