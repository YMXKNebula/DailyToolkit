using System.ComponentModel;
using System.Runtime.InteropServices;
using DailyUSE.Core.Gaming;

namespace DailyUSE.Desktop.Gaming;

internal sealed class LensPointerController : IDisposable
{
    private readonly HookProcedure _procedure;
    private readonly Func<PixelBounds?> _visibleBounds;
    private readonly Action<int,int> _move;
    private readonly Action<int> _zoom;
    private readonly Func<bool> _canMove;
    private readonly Action? _dragEnded;
    private IntPtr _hook;
    private bool _dragging,_consumedDown;
    private int _offsetX,_offsetY;

    public LensPointerController(Func<PixelBounds?> visibleBounds, Action<int,int> move, Action<int> zoom, bool install=true,
        Func<bool>? canMove=null, Action? dragEnded=null)
    {
        _visibleBounds=visibleBounds; _move=move; _zoom=zoom;
        _canMove=canMove ?? (() => true); _dragEnded=dragEnded;
        _procedure=OnMouse;
        if (!install) return;
        _hook=SetWindowsHookEx(14,_procedure,GetModuleHandle(null),0);
        if (_hook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr OnMouse(int code, IntPtr message, IntPtr pointer)
    {
        if (code >= 0)
        {
            var data=Marshal.PtrToStructure<MouseData>(pointer);
            // Movable lenses accept dragging anywhere inside; fixed lenses pass clicks through.
            // Wheel zoom remains available in both modes.
            // No rendering, I/O or GPU waits are performed in this callback.
            if (Process((int)message,data.Point.X,data.Point.Y,unchecked((short)(data.Data >> 16)))) return new(1);
        }
        return CallNextHookEx(_hook,code,message,pointer);
    }

    internal bool Process(int message,int x,int y,int delta=0)
    {
        if (message == 0x202 && _consumedDown) { CancelDrag(); _consumedDown=false; return true; }
        var bounds=_visibleBounds();
        if (bounds is null || !_canMove()) CancelDrag();
        if (bounds is null) return false;
        if (_dragging && message == 0x200) { _move(x-_offsetX,y-_offsetY); return true; }
        var inside=x >= bounds.Left && y >= bounds.Top && x < bounds.Left+bounds.Width && y < bounds.Top+bounds.Height;
        if (!inside) return false;
        if (message == 0x20A) { _zoom(delta); return true; }
        if (message != 0x201 || !_canMove()) return false;
        _offsetX=x-bounds.Left; _offsetY=y-bounds.Top;
        _dragging=_consumedDown=true;
        return true;
    }

    public void CancelDrag()
    {
        if (!_dragging) return;
        _dragging=false;
        _dragEnded?.Invoke();
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook=IntPtr.Zero; }
        CancelDrag();
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr HookProcedure(int code,IntPtr message,IntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public Point Point; public uint Data,Flags,Time; public IntPtr Extra; }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int id,HookProcedure procedure,IntPtr module,uint thread);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr message,IntPtr data);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
}
