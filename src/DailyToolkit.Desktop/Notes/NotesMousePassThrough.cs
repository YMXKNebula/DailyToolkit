using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DailyToolkit.Desktop.Notes;

// Inactive layered windows pass input to other processes. Only an explicitly
// enabled left click is consumed to focus this note; it does not also drag/select.
internal sealed class NotesMousePassThrough(Window window, Action beforeFocus, Action focus) : IDisposable
{
    private const long PassiveStyles = 0x20 | 0x08000000; // WS_EX_TRANSPARENT | WS_EX_NOACTIVATE
    private HookProcedure? _procedure;
    private IntPtr _hook;
    private bool _focused, _clickFocus, _consumeUp, _disposed;
    private int _revision;
    private IntPtr Handle => new WindowInteropHelper(window).Handle;

    internal void Update(bool focused, bool clickFocus)
    {
        _focused = focused; _clickFocus = clickFocus; _revision++;
        if (_disposed || Handle == IntPtr.Zero) return;
        SetPassive(!focused);
        var listen = window.IsVisible && !focused && clickFocus;
        if ((listen || _consumeUp) && _hook == IntPtr.Zero)
        {
            _procedure = OnMouse;
            _hook = SetWindowsHookEx(14, _procedure, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        else if (!listen && !_consumeUp) RemoveHook();
    }

    internal void PrepareKeyboardFocus() => SetPassive(false);

    private void SetPassive(bool passive)
    {
        if (_disposed || Handle == IntPtr.Zero) return;
        var style = GetWindowLongPtr(Handle, -20).ToInt64();
        var next = passive ? style | PassiveStyles : style & ~PassiveStyles;
        if (style != next) SetWindowLongPtr(Handle, -20, new(next));
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr pointer)
    {
        if (code >= 0 && (int)message == 0x0202 && _consumeUp)
        {
            _consumeUp = false;
            window.Dispatcher.BeginInvoke(new Action(() => Update(_focused, _clickFocus)), DispatcherPriority.Input);
            return new(1);
        }
        if (code >= 0 && (int)message == 0x0201 && !_focused && _clickFocus && window.IsVisible)
        {
            var point = Marshal.PtrToStructure<MouseData>(pointer).Point;
            if (GetWindowRect(Handle, out var bounds) && point.X >= bounds.Left && point.X < bounds.Right &&
                point.Y >= bounds.Top && point.Y < bounds.Bottom)
            {
                // Respect rounded/transparent corners and windows covering the note.
                SetPassive(false);
                var hit = WindowFromPoint(point);
                SetPassive(true);
                if (hit == Handle)
                {
                    _consumeUp = true; beforeFocus();
                    var revision = _revision;
                    window.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (!_disposed && revision == _revision && window.IsVisible && !_focused && _clickFocus) focus();
                    }), DispatcherPriority.Input);
                    return new(1);
                }
            }
        }
        // Movement, wheel and all other buttons are delivered normally underneath.
        return CallNextHookEx(_hook, code, message, pointer);
    }

    private void RemoveHook()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
    }
    public void Dispose() { _disposed = true; _revision++; _consumeUp = false; RemoveHook(); }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public NativePoint Point; public uint Data, Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProcedure procedure, IntPtr module, uint thread);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
}
