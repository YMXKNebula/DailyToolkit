using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Windows;
using DailyToolkit.Core.Environment;
using Microsoft.Win32;

namespace DailyToolkit.Desktop.Environment;

internal static class NativeWindowsInfo
{
    public static DisplayInfo ReadDisplay()
    {
        var scale = Math.Max(1, GetDpiForSystem() / 96d);
        var area = SystemParameters.WorkArea;
        return new(GetSystemMetrics(0), GetSystemMetrics(1), scale,
            area.Width, area.Height, SystemParameters.HighContrast);
    }

    public static SystemInfo ReadSystem()
    {
        var version = System.Environment.OSVersion.Version;
        var edition = version.Build >= 22000 ? "Windows 11" : "Windows 10";
        var release = "";
        var revision = 0;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            release = key?.GetValue("DisplayVersion") as string ?? "";
            revision = key?.GetValue("UBR") as int? ?? 0;
            var product = key?.GetValue("ProductName") as string;
            if (product?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true)
                edition = product;
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }

        return new(edition, $"{release} · {version.Build}.{revision}".Trim(' ', '·'),
            version.Build, RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            System.Environment.Version.ToString(), CultureInfo.CurrentCulture.Name);
    }

    public static CpuInfo ReadCpu()
    {
        var name = "未知处理器";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? name;
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }

        var features = new List<string>();
        if (Sse2.IsSupported) features.Add("SSE2");
        if (Avx2.IsSupported) features.Add("AVX2");
        if (AdvSimd.IsSupported) features.Add("AdvSimd");
        return new(name, System.Environment.ProcessorCount, null, features);
    }

    public static MemoryInfo ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status)
            ? new((long)status.TotalPhysical, (long)status.AvailablePhysical)
            : new(null, null);
    }

    public static PowerInfo ReadPower()
    {
        if (!GetSystemPowerStatus(out var status)) return new(null, null);
        var hasBattery = (status.BatteryFlag & 128) == 0 && status.BatteryFlag != 255;
        return new(status.AcLineStatus switch { 0 => true, 1 => false, _ => null },
            hasBattery && status.BatteryPercent <= 100 ? status.BatteryPercent : null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryPercent;
        public byte SystemStatus;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
