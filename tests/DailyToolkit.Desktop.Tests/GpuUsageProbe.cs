using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DailyToolkit.Desktop.Tests;

// Developer benchmark only. Includes the test fixture and WPF work in this process.
internal sealed class GpuUsageProbe : IDisposable
{
    private IntPtr _query, _counter;
    private readonly string _prefix = $"pid_{Process.GetCurrentProcess().Id}_";
    public GpuUsageProbe()
    {
        if (PdhOpenQuery(null, IntPtr.Zero, out _query) != 0 ||
            PdhAddEnglishCounter(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _counter) != 0)
            Dispose();
    }
    public void Begin() { if (_query != IntPtr.Zero) PdhCollectQueryData(_query); }
    public Dictionary<string, double>? Read()
    {
        if (_query == IntPtr.Zero || PdhCollectQueryData(_query) != 0) return null;
        uint bytes=0, count=0;
        const uint format=0x200 | 0x8000;
        if (PdhGetFormattedCounterArray(_counter, format, ref bytes, ref count, IntPtr.Zero) != 0x800007D2 || bytes==0) return null;
        var buffer=Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (PdhGetFormattedCounterArray(_counter, format, ref bytes, ref count, buffer) != 0) return null;
            var result=new Dictionary<string, double>();
            for (var i=0; i<count; i++)
            {
                var item=Marshal.PtrToStructure<CounterItem>(buffer + i*Marshal.SizeOf<CounterItem>());
                var name=Marshal.PtrToStringUni(item.Name);
                if (name?.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)==true && item.Value.Status<=1 && double.IsFinite(item.Value.Number))
                    result[name]=Math.Max(0,item.Value.Number);
            }
            return result.Count==0 ? null : result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public void Dispose() { if (_query!=IntPtr.Zero) PdhCloseQuery(_query); _query=IntPtr.Zero; }
    [StructLayout(LayoutKind.Sequential)] private struct CounterValue { public uint Status; public double Number; }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [DllImport("pdh.dll",CharSet=CharSet.Unicode)] private static extern uint PdhOpenQuery(string? source,IntPtr data,out IntPtr query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode)] private static extern uint PdhAddEnglishCounter(IntPtr query,string path,IntPtr data,out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll",CharSet=CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArray(IntPtr counter,uint format,ref uint bytes,ref uint count,IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
