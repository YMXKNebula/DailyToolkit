using System.ComponentModel;
using System.Runtime.InteropServices;
using DailyToolkit.Core.Gaming;

namespace DailyToolkit.Desktop.Gaming;

// Developer verification captures only the application's own test backdrop.
internal static class LensDesktopSnapshot
{
    public static byte[] Read(PixelBounds bounds)
    {
        DwmFlush();
        var screen=GetDC(IntPtr.Zero);
        var dc=CreateCompatibleDC(screen);
        IntPtr bitmap=IntPtr.Zero,previous=IntPtr.Zero;
        try
        {
            var info=new BitmapInfo { Size=(uint)Marshal.SizeOf<BitmapInfo>(),Width=bounds.Width,
                Height=-bounds.Height,Planes=1,BitCount=32 };
            bitmap=CreateDIBSection(screen,ref info,0,out var bits,IntPtr.Zero,0);
            if (bitmap == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previous=SelectObject(dc,bitmap);
            if (!BitBlt(dc,0,0,bounds.Width,bounds.Height,screen,bounds.Left,bounds.Top,0x40CC0020))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var pixels=new byte[bounds.Width*bounds.Height*4];
            Marshal.Copy(bits,pixels,0,pixels.Length);
            for (var at=3;at<pixels.Length;at+=4) pixels[at]=255;
            return pixels;
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(dc,previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            DeleteDC(dc); ReleaseDC(IntPtr.Zero,screen);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    {
        public uint Size; public int Width,Height; public ushort Planes,BitCount;
        public uint Compression,SizeImage; public int XPixels,YPixels; public uint Colors,Important;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h,IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc,IntPtr h);
    [DllImport("gdi32.dll",SetLastError=true)] private static extern bool BitBlt(IntPtr dc,int x,int y,int w,int h,IntPtr source,int sourceX,int sourceY,uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
