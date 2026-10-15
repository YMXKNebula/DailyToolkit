using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DailyToolkit.Core.FilePack;

// A private, unnamed Windows job only owns the process started for this archive
// operation. Windows closes its handle after an abnormal parent exit as well.
internal sealed class FilePackProcessJob(SafeFileHandle handle) : IDisposable
{
    public static FilePackProcessJob? Create()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
        { var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new IOException("未能建立压缩进程保护。", new Win32Exception(error)); }
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, in limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new IOException("未能配置压缩进程保护。", new Win32Exception(error)); }
        return new(handle);
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.SafeHandle))
            throw new IOException("无法保护本次压缩进程，已停止任务。", new Win32Exception(Marshal.GetLastPInvokeError()));
    }
    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public nuint Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, in ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);
}
