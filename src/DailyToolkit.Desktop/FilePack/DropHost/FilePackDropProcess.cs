using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DailyToolkit.Desktop.FilePack.DropHost;

// The elevated application keeps its own token. Only this short-lived,
// same-executable drop receiver inherits Explorer's ordinary user token.
internal static class FilePackDropProcess
{
    internal const string Argument = "--file-pack-drop-host";
    internal const string PipePrefix = "DailyToolkit.FilePack.Drop.";
    private const uint ProcessCreateProcess = 0x0080;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const uint MediumIntegrity = 0x2000, HighIntegrity = 0x3000;
    private const uint CreateSuspended = 0x00000004;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StillActive = 259;
    private const int ErrorInsufficientBuffer = 122;
    private static readonly nuint ParentProcessAttribute = 0x00020000;

    internal static bool IsStandardProcess
    {
        get
        {
            using var current = OpenProcess(ProcessQueryLimitedInformation, false, (uint)System.Environment.ProcessId);
            if (current.IsInvalid) return false;
            try { return IsStandard(ReadIdentity(current)); }
            catch (Exception exception) when (IsIdentityFailure(exception)) { return false; }
        }
    }

    internal static bool NeedsStandardDropHost
    {
        get
        {
            try
            {
                using var current = OpenProcess(ProcessQueryLimitedInformation, false, (uint)System.Environment.ProcessId);
                if (current.IsInvalid) return true;
                var owner = ReadIdentity(current);
                if (IsStandard(owner)) return false;
                if (owner.Integrity < HighIntegrity || !owner.Elevated) return true;
                var shellWindow = GetShellWindow();
                if (shellWindow == IntPtr.Zero || GetWindowThreadProcessId(shellWindow, out var shellId) == 0 || shellId == 0)
                    return true;
                using var shell = OpenProcess(ProcessQueryLimitedInformation, false, shellId);
                if (shell.IsInvalid) return true;
                var explorer = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), "explorer.exe");
                var identity = ReadIdentity(shell);
                // With UAC disabled, Explorer can already run at the application's
                // integrity level. Its drag source then needs no separate receiver.
                return !Matches(identity, owner.User, owner.Session, explorer, standard: false) ||
                    identity.Integrity < owner.Integrity;
            }
            catch (Exception exception) when (IsIdentityFailure(exception)) { return true; }
        }
    }

    internal static bool ValidatePipeName(string? pipeName) =>
        pipeName is not null && pipeName.Length == PipePrefix.Length + 32 &&
        pipeName.StartsWith(PipePrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(pipeName.AsSpan(PipePrefix.Length), "N", out var id) && id != Guid.Empty;

    internal static bool TryReadArguments(string[] args, out string pipeName, out int parentProcessId)
    {
        pipeName = string.Empty; parentProcessId = 0;
        if (args.Length != 3 || args[0] != Argument || !ValidatePipeName(args[1]) ||
            !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parent) || parent <= 0)
            return false;
        pipeName = args[1]; parentProcessId = parent; return true;
    }

    internal static Process Start(string executable, string pipeName, int parentProcessId)
    {
        var image = RequireCurrentExecutable(executable);
        if (!ValidatePipeName(pipeName)) throw new ArgumentException("拖放接收地址无效。", nameof(pipeName));
        if (parentProcessId != System.Environment.ProcessId)
            throw new ArgumentException("拖放接收端只能由当前程序启动。", nameof(parentProcessId));

        using var current = OpenProcess(ProcessQueryLimitedInformation, false, (uint)parentProcessId);
        if (current.IsInvalid) throw NativeFailure("无法读取当前程序身份。");
        var owner = ReadIdentity(current);
        if (owner.Integrity < HighIntegrity || !owner.Elevated)
            throw new InvalidOperationException("普通权限程序无需单独启动拖放接收端。");

        var shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero || GetWindowThreadProcessId(shellWindow, out var shellId) == 0 || shellId == 0)
            throw new InvalidOperationException("未找到当前桌面的普通权限资源管理器。");
        using var shell = OpenProcess(ProcessCreateProcess | ProcessQueryLimitedInformation, false, shellId);
        if (shell.IsInvalid) throw NativeFailure("无法连接当前桌面的资源管理器。");
        var shellIdentity = ReadIdentity(shell);
        var explorer = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows), "explorer.exe");
        if (!Matches(shellIdentity, owner.User, owner.Session, explorer, standard: true))
            throw new UnauthorizedAccessException("当前桌面没有可用的同用户普通权限资源管理器。");

        // Windows inherits the process token from this validated parent handle.
        // No Explorer token is copied or changed, and no handles are inherited.
        // https://devblogs.microsoft.com/oldnewthing/20190425-00/?p=102443
        using var attributes = new ParentAttributes(shell);
        var startup = new StartupInfoEx
        {
            StartupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfoEx>(),
                Flags = StartfUseShowWindow,
                ShowWindow = 0
            },
            AttributeList = attributes.Pointer
        };
        var command = new StringBuilder($"{QuoteArgument(image)} {Argument} {pipeName} {parentProcessId.ToString(CultureInfo.InvariantCulture)}");
        if (!CreateProcess(image, command, IntPtr.Zero, IntPtr.Zero, false,
                CreateSuspended | ExtendedStartupInfoPresent | CreateNoWindow, IntPtr.Zero,
                Path.GetDirectoryName(image), ref startup, out var information))
            throw NativeFailure("无法启动普通权限拖放接收端。");

        using var childHandle = new SafeProcessHandle(information.Process, true);
        using var threadHandle = new SafeWaitHandle(information.Thread, true);
        Process? process = null;
        var accepted = false;
        try
        {
            // Verify the exact CreateProcess handle before any helper code runs.
            // A failed check only terminates this newly created process handle.
            if (!GetExitCodeProcess(childHandle, out var status) || status != StillActive ||
                !Matches(ReadIdentity(childHandle), owner.User, owner.Session, image, standard: true))
                throw new UnauthorizedAccessException("拖放接收端的权限或身份不符。");
            process = Process.GetProcessById(checked((int)information.ProcessId));
            _ = process.SafeHandle; // Cache its handle while our creation handle pins the process object.
            if (ResumeThread(threadHandle) != 1)
                throw NativeFailure("拖放接收端未能开始运行。");
            accepted = true;
            return process;
        }
        finally
        {
            if (!accepted)
            {
                _ = TerminateProcess(childHandle, 1);
                process?.Dispose();
            }
        }
    }

    // This queries the actual primary token user, rather than its default owner
    // (which can be Administrators for the same user after elevation).
    internal static bool IsTrustedPeer(uint processId, string executable, int sessionId, bool standard)
    {
        if (processId == 0 || processId > int.MaxValue || sessionId < 0) return false;
        try
        {
            var image = RequireCurrentExecutable(executable);
            using var current = OpenProcess(ProcessQueryLimitedInformation, false, (uint)System.Environment.ProcessId);
            using var peer = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (current.IsInvalid || peer.IsInvalid) return false;
            var owner = ReadIdentity(current);
            return owner.Session == (uint)sessionId &&
                GetExitCodeProcess(peer, out var status) && status == StillActive &&
                Matches(ReadIdentity(peer), owner.User, owner.Session, image, standard);
        }
        catch (Exception exception) when (IsIdentityFailure(exception)) { return false; }
    }

    private static bool Matches(ProcessIdentity identity, SecurityIdentifier user, uint session, string image, bool standard) =>
        identity.User == user && identity.Session == session &&
        string.Equals(identity.Image, Path.GetFullPath(image), StringComparison.OrdinalIgnoreCase) &&
        (standard ? IsStandard(identity) : identity.Integrity >= HighIntegrity && identity.Elevated);

    private static bool IsStandard(ProcessIdentity identity) =>
        identity.Integrity is >= MediumIntegrity and < HighIntegrity && !identity.Elevated;

    private static string RequireCurrentExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Contains('\0') || !Path.IsPathFullyQualified(executable) ||
            System.Environment.ProcessPath is not { } current)
            throw new ArgumentException("拖放接收端必须使用当前程序。", nameof(executable));
        var image = Path.GetFullPath(executable);
        if (!string.Equals(image, Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("拖放接收端必须使用当前程序。", nameof(executable));
        return image;
    }

    private static ProcessIdentity ReadIdentity(SafeProcessHandle process)
    {
        var image = new StringBuilder(32768); var length = image.Capacity;
        if (!QueryFullProcessImageName(process, 0, image, ref length))
            throw NativeFailure("无法读取程序路径。");
        if (!OpenProcessToken(process, TokenQuery, out var token))
            throw NativeFailure("无法读取程序权限。");
        using (token)
        {
            var user = ReadTokenInformation(token, 1, buffer => new SecurityIdentifier(Marshal.ReadIntPtr(buffer)));
            var session = ReadTokenInformation(token, 12, buffer => unchecked((uint)Marshal.ReadInt32(buffer)));
            var elevated = ReadTokenInformation(token, 20, buffer => Marshal.ReadInt32(buffer) != 0);
            var integrity = ReadTokenInformation(token, 25, buffer =>
            {
                var sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer));
                // Integrity SIDs have exactly the mandatory-label authority and one RID.
                var bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
                if (bytes.Length != 12 || bytes[1] != 1 ||
                    !sid.Value.StartsWith("S-1-16-", StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("程序完整性级别无效。");
                return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
            });
            return new ProcessIdentity(user, session, integrity, elevated, Path.GetFullPath(image.ToString()));
        }
    }

    private static T ReadTokenInformation<T>(SafeAccessTokenHandle token, int informationClass, Func<IntPtr, T> read)
    {
        // TOKEN_ELEVATION is exactly one DWORD. Windows rejects a zero-length
        // probe with ERROR_BAD_LENGTH rather than ERROR_INSUFFICIENT_BUFFER.
        var length = sizeof(uint);
        if (informationClass != 20 &&
            (GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out length) ||
             Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || length is < 4 or > 65536))
            throw NativeFailure("无法读取程序身份。");
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetTokenInformation(token, informationClass, buffer, length, out var actual) || actual > length || actual < 4)
                throw NativeFailure("无法读取程序身份。");
            return read(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static bool IsIdentityFailure(Exception exception) =>
        exception is Win32Exception or ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException;

    private static Win32Exception NativeFailure(string message) => new(Marshal.GetLastWin32Error(), message);

    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\""); var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private sealed record ProcessIdentity(SecurityIdentifier User, uint Session, uint Integrity, bool Elevated, string Image);

    private sealed class ParentAttributes : IDisposable
    {
        internal IntPtr Pointer { get; private set; }
        private IntPtr _parent;
        private bool _initialized;
        internal ParentAttributes(SafeProcessHandle parent)
        {
            nuint bytes = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || bytes is 0 or > 65536)
                throw NativeFailure("无法准备拖放接收端。");
            try
            {
                Pointer = Marshal.AllocHGlobal(checked((int)bytes));
                if (!InitializeProcThreadAttributeList(Pointer, 1, 0, ref bytes))
                    throw NativeFailure("无法准备拖放接收端。");
                _initialized = true;
                _parent = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(_parent, parent.DangerousGetHandle());
                if (!UpdateProcThreadAttribute(Pointer, 0, ParentProcessAttribute, _parent, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                    throw NativeFailure("无法设置拖放接收端权限。");
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (Pointer != IntPtr.Zero)
            {
                if (_initialized) DeleteProcThreadAttributeList(Pointer);
                Marshal.FreeHGlobal(Pointer); Pointer = IntPtr.Zero;
            }
            if (_parent != IntPtr.Zero) { Marshal.FreeHGlobal(_parent); _parent = IntPtr.Zero; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal int Size;
        internal IntPtr Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal short ShowWindow, ReservedBytes;
        internal IntPtr Reserved2, StandardInput, StandardOutput, StandardError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { internal StartupInfo StartupInfo; internal IntPtr AttributeList; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, IntPtr information, int length, out int returned);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nuint attribute, IntPtr value, nuint bytes, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string? directory,
        ref StartupInfoEx startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
}
