using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DailyToolkit.Desktop.FilePack.DropHost;

// A dedicated client area, outside scrolling content. No transparent overlay,
// global input hook or change to the main process's integrity level is needed.
internal sealed class FilePackDropSurface : HwndHost
{
    private IntPtr _child;
    private readonly uint _expectedProcessId;
    private readonly Func<bool>? _isHostAlive;

    // The production caller supplies its retained helper process identity.
    // This overload also permits the existing isolated native-window tests.
    internal FilePackDropSurface(IntPtr child) : this(child, ReadOwnerProcessId(child)) { }
    internal FilePackDropSurface(IntPtr child, int expectedProcessId, Func<bool>? isHostAlive = null)
    {
        if (child == IntPtr.Zero || expectedProcessId <= 0) throw new ArgumentException("拖放接收端窗口身份无效。", nameof(child));
        _child = child; _expectedProcessId = (uint)expectedProcessId; _isHostAlive = isHostAlive;
    }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        if (!IsOwnedWindow(_child) ||
            !AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(parent.Handle), GetWindowDpiAwarenessContext(_child)))
            throw new InvalidOperationException("拖放接收端的窗口显示模式不匹配。");
        var style = GetWindowLong(_child, -16);
        RequireOwnedWindow();
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLong(_child, -16, (style & ~unchecked((int)0x80000000)) | 0x40000000) == 0 && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        RequireOwnedWindow();
        Marshal.SetLastPInvokeError(0);
        if (SetParent(_child, parent.Handle) == IntPtr.Zero && Marshal.GetLastPInvokeError() != 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        RequireOwnedWindow();
        ShowWindow(_child, 4); return new(this, _child);
    }

    protected override void DestroyWindowCore(HandleRef window)
    {
        var child = _child; _child = IntPtr.Zero;
        if (child == IntPtr.Zero) return;
        // Never call DestroyWindow on another process's HWND.
        if (IsOwnedWindow(child)) ShowWindow(child, 0);
        if (IsOwnedWindow(child)) SetParent(child, IntPtr.Zero);
    }

    private void RequireOwnedWindow()
    { if (!IsOwnedWindow(_child)) throw new InvalidOperationException("拖放接收端窗口已关闭。"); }

    private bool IsOwnedWindow(IntPtr window) => window != IntPtr.Zero &&
        (_isHostAlive?.Invoke() ?? true) && IsWindow(window) &&
        GetWindowThreadProcessId(window, out var owner) != 0 && owner == _expectedProcessId;

    private static int ReadOwnerProcessId(IntPtr child) =>
        GetWindowThreadProcessId(child, out var owner) != 0 && owner <= int.MaxValue ? (int)owner : 0;

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}
