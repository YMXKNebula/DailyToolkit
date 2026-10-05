using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace DailyToolkit.Desktop.Gaming;

internal static class CaptureInterop
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateCapture(IntPtr factory, IntPtr target, ref Guid iid, out IntPtr item);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDxgiInterface(IntPtr access, ref Guid iid, out IntPtr result);

    public static GraphicsCaptureItem ForMonitor(IntPtr monitor) => Create(monitor, 4);
    public static GraphicsCaptureItem ForWindow(IntPtr window) => Create(window, 3);

    private static GraphicsCaptureItem Create(IntPtr target, int slot)
    {
        const string name = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(name, name.Length, out var className));
        IntPtr factory = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            var interopId = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, ref interopId, out factory));
            var create = Marshal.GetDelegateForFunctionPointer<CreateCapture>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(factory), slot * IntPtr.Size));
            var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
            Marshal.ThrowExceptionForHR(create(factory, target, ref itemId, out item));
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(item);
        }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            WindowsDeleteString(className);
        }
    }

    public static IDirect3DDevice Wrap(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var pointer));
        try { return MarshalInterface<IDirect3DDevice>.FromAbi(pointer); }
        finally { Marshal.Release(pointer); }
    }

    public static ID3D11Texture2D Texture(IDirect3DSurface surface)
    {
        var accessId = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
        var native = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in accessId, out var access));
        try
        {
            var get = Marshal.GetDelegateForFunctionPointer<GetDxgiInterface>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(access), 3 * IntPtr.Size));
            var textureId = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
            Marshal.ThrowExceptionForHR(get(access, ref textureId, out var pointer));
            return new ID3D11Texture2D(pointer);
        }
        finally { Marshal.Release(access); }
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string value, int length, out IntPtr result);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr value);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr name, ref Guid iid, out IntPtr result);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr device, out IntPtr result);
}
