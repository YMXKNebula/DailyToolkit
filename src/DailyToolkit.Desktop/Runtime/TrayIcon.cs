using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
namespace DailyToolkit.Desktop.Runtime;

internal sealed class TrayIcon : IDisposable
{
    private const int Callback=0x802A;
    private readonly HwndSource _source;
    private readonly uint _taskbarCreated=RegisterWindowMessage("TaskbarCreated");
    private Data _data;
    private bool _added;
    private readonly DispatcherTimer _retry=new(DispatcherPriority.Background) { Interval=TimeSpan.FromSeconds(2) };
    internal bool Available => _added;
    internal event Action? ShowRequested,SettingsRequested,ExitRequested;
    internal TrayIcon(HwndSource source)
    {
        _source=source;
        ExtractIconEx(System.Environment.ProcessPath!,0,out var large,out var small,1);
        if (large != IntPtr.Zero) DestroyIcon(large);
        var ownIcon=small != IntPtr.Zero;
        _data=new() { Size=(uint)Marshal.SizeOf<Data>(),Window=source.Handle,Id=1,Flags=1|2|4|128,
            CallbackMessage=Callback,Icon=ownIcon ? small : LoadIcon(IntPtr.Zero,new IntPtr(32512)),Tip="DailyToolkit",Info="",InfoTitle="" };
        _ownsIcon=ownIcon;
        ChangeWindowMessageFilterEx(source.Handle,_taskbarCreated,1,IntPtr.Zero);
        _retry.Tick += (_,_) => Add();
        _source.AddHook(Hook); Add();
    }
    private readonly bool _ownsIcon;
    private void Add()
    {
        _added=ShellNotifyIcon(0,ref _data);
        if (_added) { _data.Version=4; ShellNotifyIcon(4,ref _data); _retry.Stop(); }
        else _retry.Start();
    }
    private IntPtr Hook(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if ((uint)message == _taskbarCreated) { Add(); return IntPtr.Zero; }
        if (message != Callback) return IntPtr.Zero;
        handled=true;
        var action=(int)(lParam.ToInt64() & 0xFFFF);
        if (action is 0x400 or 0x401 or 0x203) ShowRequested?.Invoke();
        else if (action == 0x7B) ShowMenu();
        return IntPtr.Zero;
    }
    private void ShowMenu()
    {
        var menu=CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu,0,1,"打开 DailyToolkit"); AppendMenu(menu,0,2,"软件设置"); AppendMenu(menu,0x800,0,null); AppendMenu(menu,0,3,"退出");
            GetCursorPos(out var cursor); SetForegroundWindow(_source.Handle);
            var command=TrackPopupMenu(menu,0x100|2,cursor.X,cursor.Y,0,_source.Handle,IntPtr.Zero);
            PostMessage(_source.Handle,0,IntPtr.Zero,IntPtr.Zero);
            if (command == 1) ShowRequested?.Invoke(); else if (command == 2) SettingsRequested?.Invoke(); else if (command == 3) ExitRequested?.Invoke();
        }
        finally { DestroyMenu(menu); }
    }
    public void Dispose()
    {
        _source.RemoveHook(Hook);
        _retry.Stop();
        if (_added) { ShellNotifyIcon(2,ref _data); _added=false; }
        if (_ownsIcon && _data.Icon != IntPtr.Zero) { DestroyIcon(_data.Icon); _data.Icon=IntPtr.Zero; }
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct Data
    {
        public uint Size; public IntPtr Window; public uint Id,Flags,CallbackMessage; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X,Y; }
    [DllImport("shell32.dll",EntryPoint="Shell_NotifyIconW",CharSet=CharSet.Unicode)] private static extern bool ShellNotifyIcon(uint message,ref Data data);
    [DllImport("shell32.dll",EntryPoint="ExtractIconExW",CharSet=CharSet.Unicode)] private static extern uint ExtractIconEx(string file,int index,out IntPtr large,out IntPtr small,uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll",EntryPoint="LoadIconW")] private static extern IntPtr LoadIcon(IntPtr instance,IntPtr resource);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll",EntryPoint="AppendMenuW",CharSet=CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu,uint flags,uint id,string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr menu,uint flags,int x,int y,int reserved,IntPtr window,IntPtr rectangle);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool ChangeWindowMessageFilterEx(IntPtr window,uint message,uint action,IntPtr change);
}
