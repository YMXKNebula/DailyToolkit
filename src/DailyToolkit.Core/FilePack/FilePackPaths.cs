using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DailyToolkit.Core.FilePack;

public static class FilePackPaths
{
    public const int MaximumEntries = 100_000;
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
      "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³" };

    public static string NormalizeArchivePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/');
        if (normalized.Length is 0 or > 30_000 || normalized[0] == '/' || normalized.Contains(':'))
            throw new ArgumentException("包内路径必须是相对路径。", nameof(path));
        foreach (var part in normalized.Split('/'))
        {
            if (part.Length is 0 or > 255 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.Any(character => char.IsControl(character) || "<>\"|?*".Contains(character)) ||
                ReservedNames.Contains(part.Split('.')[0].TrimEnd(' ')))
                throw new ArgumentException($"包内路径包含无效名称：{path}", nameof(path));
        }
        return normalized;
    }

    public static string NormalizeSourcePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            if (path.Length < 7 || !char.IsAsciiLetter(path[4]) || path[5] != ':' || path[6] != '\\')
                throw new ArgumentException("不支持设备命名空间路径。", nameof(path));
            path = path[4..];
        }
        else if (path.StartsWith(@"\\.\", StringComparison.Ordinal)) throw new ArgumentException("不支持设备路径。", nameof(path));
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool IsWithin(string path, string root)
    {
        var fullPath = NormalizeSourcePath(path); var fullRoot = NormalizeSourcePath(root);
        return fullPath.StartsWith(Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool AreSameFile(string firstPath, string secondPath)
    {
        var first = NormalizeSourcePath(firstPath); var second = NormalizeSourcePath(secondPath);
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
        EnsureNoReparsePoints(first); EnsureNoReparsePoints(second);
        if (!File.Exists(first) || !File.Exists(second)) return false;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("文件身份检查仅支持 Windows。");
        using var firstHandle = File.OpenHandle(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var secondHandle = File.OpenHandle(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // File IDs also identify short-name, SUBST and hard-link aliases. Hashes would
        // confuse distinct files with identical contents and needlessly read all data.
        if (!GetFileInformationByHandleEx(firstHandle, 18, out var firstIdentity, (uint)Marshal.SizeOf<FileIdentity>()) ||
            !GetFileInformationByHandleEx(secondHandle, 18, out var secondIdentity, (uint)Marshal.SizeOf<FileIdentity>()))
            throw new IOException("无法确认文件身份，已阻止可能覆盖原件的操作。", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        return firstIdentity.VolumeSerialNumber == secondIdentity.VolumeSerialNumber && firstIdentity.FileId == secondIdentity.FileId;
    }

    public static void EnsureNoReparsePoints(string path)
    {
        var current = NormalizeSourcePath(path);
        while (true)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"不处理符号链接或目录联接点：{current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || parent == current) break;
            current = parent;
        }
    }

    public static PackSourceStamp CaptureSourceStamp(string path)
    {
        EnsureNoReparsePoints(path);
        var info = new FileInfo(NormalizeSourcePath(path));
        info.Refresh();
        if (!info.Exists || (info.Attributes & FileAttributes.Directory) != 0)
            throw new FileNotFoundException("源文件不存在或无法读取。", path);
        return new(info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks);
    }

    public static void ValidateSource(PackEntry entry)
    {
        if (entry.IsDirectory && entry.SourcePath.Length == 0) return;
        var source = NormalizeSourcePath(entry.SourcePath);
        var root = NormalizeSourcePath(entry.SourceRoot);
        if (!IsWithin(source, root)) throw new IOException($"源文件超出记录的来源目录：{entry.SourcePath}");
        EnsureNoReparsePoints(root); EnsureNoReparsePoints(source);
        if (entry.IsDirectory)
        {
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"源目录不存在：{source}");
            if (Directory.EnumerateFileSystemEntries(source).Any())
                throw new IOException($"原空目录已出现内容，请重新收集：{source}");
        }
        else if (entry.SourceStamp is null || entry.SourceStamp.Length != entry.Size || CaptureSourceStamp(source) != entry.SourceStamp)
            throw new IOException($"源文件在收集后发生变化或无法读取：{source}");
    }

    public static void ValidateEntries(IEnumerable<PackEntry> entries)
    {
        var occupied = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<Guid>(); var count = 0;
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.SourcePath is null || entry.SourceRoot is null) throw new ArgumentException("源路径信息无效。");
            if (++count > MaximumEntries) throw new InvalidOperationException($"一次最多收集 {MaximumEntries:N0} 个条目。");
            if (entry.Id == Guid.Empty || !ids.Add(entry.Id)) throw new ArgumentException("文件条目标识无效或重复。");
            var path = NormalizeArchivePath(entry.ArchivePath);
            if (entry.Size < 0 || entry.IsDirectory && (entry.Size != 0 || entry.SourceStamp is not null))
                throw new ArgumentException("文件条目的大小或类型无效。");
            if (entry.SourcePath.Length == 0 && (!entry.IsDirectory || entry.SourceRoot.Length != 0))
                throw new ArgumentException("只有包内文件夹可以没有原始路径。");
            if (!occupied.TryAdd(path, entry.IsDirectory)) throw new ArgumentException($"包内路径重复：{path}");
            var parent = path;
            while (parent.LastIndexOf('/') is var split && split >= 0)
            {
                parent = parent[..split];
                if (occupied.TryGetValue(parent, out var directory) && !directory)
                    throw new ArgumentException($"文件和目录的包内路径冲突：{parent}");
            }
        }
        foreach (var path in occupied.Keys)
        {
            // Only ancestors need inspecting; avoid a quadratic scan of the manifest.
            foreach (var part in Ancestors(path))
                if (occupied.TryGetValue(part, out var ancestorIsDirectory) && !ancestorIsDirectory)
                    throw new ArgumentException($"文件和目录的包内路径冲突：{part}");
        }
    }

    internal static IEnumerable<string> Ancestors(string path)
    {
        while (path.LastIndexOf('/') is var split && split >= 0) { path = path[..split]; yield return path; }
    }

    public static string GetOutputPath(PackOptions options)
    {
        if (!Enum.IsDefined(options.Format) || !Enum.IsDefined(options.Mode) || !Enum.IsDefined(options.Compression) ||
            !Enum.IsDefined(options.OutputPolicy)) throw new ArgumentException("打包选项无效。");
        var name = NormalizeArchivePath(options.UseCompletionTimeName ? PackArchiveNaming.DefaultName : options.ArchiveName);
        if (name.Contains('/')) throw new ArgumentException("压缩包名称不能包含目录。");
        var extension = options.Format switch { PackFormat.Zip => ".zip", PackFormat.SevenZip => ".7z", _ => ".tar" };
        foreach (var known in new[] { ".zip", ".7z", ".tar" })
            if (name.EndsWith(known, StringComparison.OrdinalIgnoreCase)) { name = name[..^known.Length]; break; }
        name = NormalizeArchivePath(name + extension);
        var directory = NormalizeSourcePath(options.OutputDirectory);
        var output = NormalizeSourcePath(Path.Combine(directory, name));
        if (!IsWithin(output, directory)) throw new ArgumentException("压缩包输出路径超出所选目录。");
        return output;
    }

    public static void EnsureAvailableSpace(string directory, long requiredBytes)
    {
        if (requiredBytes < 0) throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        var root = Path.GetPathRoot(NormalizeSourcePath(directory))!;
        var free = new DriveInfo(root).AvailableFreeSpace;
        if (free < requiredBytes) throw new IOException($"临时目录空间不足，需要 {requiredBytes:N0} 字节，可用 {free:N0} 字节。");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdentity { public ulong VolumeSerialNumber; public Guid FileId; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileIdentity information, uint bufferSize);
}
