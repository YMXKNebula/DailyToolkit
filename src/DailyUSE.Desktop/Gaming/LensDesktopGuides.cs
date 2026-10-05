using System.ComponentModel;
using System.Runtime.InteropServices;
using DailyUSE.Core.Gaming;

namespace DailyUSE.Desktop.Gaming;

// Two thin desktop windows, rather than lines rendered into the magnified texture.
internal sealed class LensDesktopGuides : IDisposable
{
    private const string ClassName="DailyUSE.LensGuides";
    private static readonly WindowProcedure Procedure=WindowProc;
    private static readonly Dictionary<IntPtr,LensDesktopGuides> Windows=new();
    private static bool _registered;
    private readonly PixelBounds _monitor;
    private PixelBounds _frame=new(0,0,0,0);
    private bool _verticalAligned,_horizontalAligned;
    public IntPtr VerticalHandle { get; private set; }
    public IntPtr HorizontalHandle { get; private set; }
    public bool Visible { get; private set; }

    public LensDesktopGuides(PixelBounds monitor)
    {
        _monitor=monitor;
        if (!_registered)
        {
            var type=new WindowClass { Size=(uint)Marshal.SizeOf<WindowClass>(),Procedure=Procedure,
                Instance=GetModuleHandle(null),ClassName=ClassName };
            if (RegisterClassEx(ref type) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            _registered=true;
        }
        try
        {
            VerticalHandle=Create(monitor.Left+monitor.Width/2-1,monitor.Top,2,monitor.Height);
            HorizontalHandle=Create(monitor.Left,monitor.Top+monitor.Height/2-1,monitor.Width,2);
        }
        catch { Dispose(); throw; }
    }

    private IntPtr Create(int x,int y,int width,int height)
    {
        var handle=CreateWindowEx(0x080800A8,ClassName,"DailyUSE 对齐辅助线",0x80000000,
            x,y,width,height,IntPtr.Zero,IntPtr.Zero,GetModuleHandle(null),IntPtr.Zero);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        Windows.Add(handle,this);
        if (!SetLayeredWindowAttributes(handle,0x00FF00FF,255,1) || !SetWindowDisplayAffinity(handle,0x11))
        {
            var error=new Win32Exception(Marshal.GetLastWin32Error());
            Windows.Remove(handle); DestroyWindow(handle); throw error;
        }
        return handle;
    }

    public void Update(PixelBounds frame,bool verticalAligned,bool horizontalAligned)
    {
        _frame=frame; _verticalAligned=verticalAligned; _horizontalAligned=horizontalAligned;
        Visible=true;
        foreach (var handle in new[] { VerticalHandle,HorizontalHandle })
        {
            InvalidateRect(handle,IntPtr.Zero,false);
            SetWindowPos(handle,new IntPtr(-1),0,0,0,0,0x0010 | 0x0040 | 0x0001 | 0x0002);
        }
    }

    public void Hide()
    {
        Visible=false;
        ShowWindow(VerticalHandle,0); ShowWindow(HorizontalHandle,0);
    }

    private void Paint(IntPtr handle)
    {
        var dc=BeginPaint(handle,out var paint);
        try
        {
            var vertical=handle == VerticalHandle;
            var extent=vertical ? _monitor.Height : _monitor.Width;
            var clear=CreateSolidBrush(0x00FF00FF);
            var line=CreateSolidBrush((vertical ? _verticalAligned : _horizontalAligned) ? 0x0059D4FFu : 0x00CBC5B6u);
            try
            {
                var all=new NativeRect { Right=vertical ? 2 : extent,Bottom=vertical ? extent : 2 };
                FillRect(dc,ref all,clear);
                var center=vertical ? _monitor.Left+_monitor.Width/2 : _monitor.Top+_monitor.Height/2;
                var crossing=vertical ? center >= _frame.Left && center < _frame.Left+_frame.Width
                    : center >= _frame.Top && center < _frame.Top+_frame.Height;
                var gapStart=vertical ? _frame.Top-_monitor.Top : _frame.Left-_monitor.Left;
                var gapEnd=gapStart+(vertical ? _frame.Height : _frame.Width);
                for (var start=0;start<extent;start+=12)
                {
                    var end=Math.Min(start+7,extent);
                    if (!crossing) Draw(start,end);
                    else
                    {
                        Draw(start,Math.Min(end,gapStart));
                        Draw(Math.Max(start,gapEnd),end);
                    }
                }
                void Draw(int start,int end)
                {
                    if (end <= start) return;
                    var rect=vertical ? new NativeRect { Top=start,Right=2,Bottom=end }
                        : new NativeRect { Left=start,Right=end,Bottom=2 };
                    FillRect(dc,ref rect,line);
                }
            }
            finally { DeleteObject(clear); DeleteObject(line); }
        }
        finally { EndPaint(handle,ref paint); }
    }

    private static IntPtr WindowProc(IntPtr handle,uint message,IntPtr wParam,IntPtr lParam)
    {
        if (message == 0x0084) return new(-1);
        if (message == 0x0021) return new(3);
        if (message == 0x000F && Windows.TryGetValue(handle,out var guides))
        {
            guides.Paint(handle); return IntPtr.Zero;
        }
        return DefWindowProc(handle,message,wParam,lParam);
    }

    public void Dispose()
    {
        Visible=false;
        foreach (var handle in new[] { VerticalHandle,HorizontalHandle })
            if (handle != IntPtr.Zero) { Windows.Remove(handle); DestroyWindow(handle); }
        VerticalHandle=HorizontalHandle=IntPtr.Zero;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate IntPtr WindowProcedure(IntPtr h,uint m,IntPtr w,IntPtr l);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct PaintStruct
    {
        public IntPtr Dc; public int Erase; public NativeRect Paint; public int Restore,IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray,SizeConst=32)] public byte[] Reserved;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct WindowClass
    {
        public uint Size,Style; public WindowProcedure? Procedure; public int ClassExtra,WindowExtra;
        public IntPtr Instance,Icon,Cursor,Background; public string? MenuName,ClassName; public IntPtr SmallIcon;
    }
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern ushort RegisterClassEx(ref WindowClass type);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr CreateWindowEx(uint ex,string type,string title,uint style,int x,int y,int w,int h,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h,uint m,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h,int command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetLayeredWindowAttributes(IntPtr h,uint key,byte alpha,uint flags);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowDisplayAffinity(IntPtr h,uint affinity);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr h,IntPtr rect,bool erase);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr h,out PaintStruct paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h,ref PaintStruct paint);
    [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc,ref NativeRect rect,IntPtr brush);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
