using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    // Available on updated Windows 10/11; null preserves an explicit unsupported
    // result rather than substituting total working set for private working set.
    private static long? ReadPrivateWorkingSet(Process process)
    {
        var counters = new ProfileMemoryCounters { Size = (uint)Marshal.SizeOf<ProfileMemoryCounters>() };
        return GetProfileMemoryInfo(process.Handle, ref counters, counters.Size)
            ? checked((long)counters.PrivateWorkingSetSize) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProfileMemoryCounters
    {
        internal uint Size, PageFaultCount;
        internal nuint PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
            QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage,
            PrivateUsage, PrivateWorkingSetSize;
        internal ulong SharedCommitUsage;
    }

    [DllImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProfileMemoryInfo(IntPtr process, ref ProfileMemoryCounters counters, uint size);
}
