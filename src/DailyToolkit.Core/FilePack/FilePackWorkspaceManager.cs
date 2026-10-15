using System.Buffers;
using System.Text.Json;

namespace DailyToolkit.Core.FilePack;

public sealed class FilePackWorkspaceManager
{
    private const string MarkerName = ".filepack-owner.json";
    private readonly SemaphoreSlim _operations = new(1, 1);
    public string RootDirectory { get; }
    public FilePackWorkspaceManager(string rootDirectory) => RootDirectory = FilePackPaths.NormalizeSourcePath(rootDirectory);

    public string GetWorkspacePath(PackSnapshot snapshot) => Path.Combine(GetSessionPath(snapshot), "staging");
    private string GetSessionPath(PackSnapshot snapshot)
    {
        if (snapshot.TaskId == Guid.Empty || snapshot.SessionId == Guid.Empty) throw new ArgumentException("任务或工作区标识无效。");
        var session = FilePackPaths.NormalizeSourcePath(Path.Combine(RootDirectory, "Sessions", snapshot.SessionId.ToString("N")));
        if (!FilePackPaths.IsWithin(session, RootDirectory)) throw new IOException("工作区路径超出模块目录。");
        foreach (var entry in snapshot.Entries)
        {
            if (entry.SourcePath.Length == 0) continue;
            var source = FilePackPaths.NormalizeSourcePath(entry.SourcePath);
            if (string.Equals(source, session, StringComparison.OrdinalIgnoreCase) || FilePackPaths.IsWithin(source, session) ||
                entry.IsDirectory && FilePackPaths.IsWithin(session, source))
                throw new IOException("临时工作区不能与原始文件或目录重叠。");
        }
        return session;
    }

    public async Task<string> PrepareAsync(PackSnapshot snapshot, IProgress<PackProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => new PackPlanner().Create(snapshot), cancellationToken).ConfigureAwait(false);
            if (snapshot.Options.Mode == PackMode.BuiltIn && !snapshot.Options.AllowStaging)
                throw new InvalidOperationException("此任务需要临时复制，请先确认额外空间需求。");
            var session = GetSessionPath(snapshot); var staging = GetWorkspacePath(snapshot);
            var bytes = snapshot.Entries.Aggregate(0L, (total, entry) => checked(total + entry.Size));
            // Account for filesystem metadata and small external-compressor bookkeeping.
            var reserve = Math.Max(16L * 1024 * 1024, bytes / 100);
            FilePackPaths.EnsureNoReparsePoints(session);
            FilePackPaths.EnsureAvailableSpace(RootDirectory, checked(bytes + reserve));
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(session))
            {
                if (!await ValidateOwnerAsync(snapshot, requireStaging: false, cancellationToken).ConfigureAwait(false))
                    throw new IOException("临时工作区缺少有效归属标记，已阻止写入。");
                if (Directory.Exists(staging) || File.Exists(staging))
                    throw new IOException("此任务已有临时文件，请保留或显式清理后重试。");
            }
            else
            {
                Directory.CreateDirectory(session); FilePackPaths.EnsureNoReparsePoints(session);
                if (Directory.EnumerateFileSystemEntries(session).Any()) throw new IOException("工作区目录已被占用。");
                var marker = JsonSerializer.SerializeToUtf8Bytes(new OwnerMarker(1, snapshot.TaskId, snapshot.SessionId));
                await using var stream = new FileStream(Path.Combine(session, MarkerName), FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
                // Finish the small ownership record even if cancellation arrives now.
                // Otherwise a cancelled preparation could leave an unidentifiable folder.
                await stream.WriteAsync(marker).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            FilePackPaths.EnsureNoReparsePoints(staging); Directory.CreateDirectory(staging);
            var processedBytes = 0L; var processedFiles = 0; var processedEntries = 0;
            var lastReport = long.MinValue;
            void Report(string detail, bool force = false)
            {
                var now = System.Environment.TickCount64;
                if (!force && lastReport != long.MinValue && now - lastReport < 100) return;
                lastReport = now;
                progress?.Report(new(PackState.Copying, bytes > 0 ? processedBytes * 100d / bytes :
                    processedEntries * 100d / snapshot.Entries.Count, processedBytes, processedFiles, detail));
            }
            Report("正在准备临时文件。", force: true);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                foreach (var entry in snapshot.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FilePackPaths.ValidateSource(entry);
                    var target = FilePackPaths.NormalizeSourcePath(Path.Combine(staging,
                        FilePackPaths.NormalizeArchivePath(entry.ArchivePath).Replace('/', Path.DirectorySeparatorChar)));
                    if (!FilePackPaths.IsWithin(target, staging)) throw new IOException("包内路径逃逸临时目录。");
                    FilePackPaths.EnsureNoReparsePoints(target);
                    if (entry.IsDirectory) Directory.CreateDirectory(target);
                    else
                    {
                        FilePackPaths.EnsureAvailableSpace(RootDirectory, checked(bytes - processedBytes + reserve));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        FilePackPaths.EnsureNoReparsePoints(target);
                        try
                        {
                            await using (var source = new FileStream(entry.SourcePath, FileMode.Open, FileAccess.Read,
                                FileShare.Read, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
                            await using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write,
                                FileShare.None, buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
                            {
                                if (source.Length != entry.Size) throw new IOException("源文件大小发生变化。");
                                long copied = 0;
                                while (true)
                                {
                                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                                    if (read == 0) break;
                                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                                    copied = checked(copied + read); processedBytes = checked(processedBytes + read);
                                    if (copied > entry.Size) throw new IOException("源文件在复制期间增长。");
                                    Report(entry.ArchivePath);
                                }
                                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                                if (copied != entry.Size) throw new IOException("源文件在复制期间发生变化。");
                            }
                            FilePackPaths.ValidateSource(entry);
                            processedFiles++;
                        }
                        catch (IOException exception) { throw new IOException($"未能复制 {entry.SourcePath}：{exception.Message}", exception); }
                        catch (UnauthorizedAccessException exception) { throw new IOException($"无法读取或复制 {entry.SourcePath}：{exception.Message}", exception); }
                    }
                    processedEntries++; Report(entry.ArchivePath);
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
            cancellationToken.ThrowIfCancellationRequested();
            Report("临时文件已准备完成。", force: true);
            return staging;
        }
        finally { _operations.Release(); }
    }

    public async Task<bool> IsOwnedAsync(PackSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        try { return await ValidateOwnerAsync(snapshot, requireStaging: true, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (IsOwnershipError(exception)) { return false; }
    }

    private async Task<bool> ValidateOwnerAsync(PackSnapshot snapshot, bool requireStaging, CancellationToken token)
    {
        var session = GetSessionPath(snapshot); var marker = Path.Combine(session, MarkerName);
        FilePackPaths.EnsureNoReparsePoints(marker);
        if (!Directory.Exists(session) || !File.Exists(marker) || new FileInfo(marker).Length > 4096) return false;
        await using var markerStream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (markerStream.Length > 4096) return false;
        var owner = await JsonSerializer.DeserializeAsync<OwnerMarker>(markerStream, cancellationToken: token).ConfigureAwait(false);
        if (owner is null || owner.Version != 1 || owner.TaskId != snapshot.TaskId || owner.SessionId != snapshot.SessionId) return false;
        if (requireStaging)
        {
            var staging = GetWorkspacePath(snapshot); FilePackPaths.EnsureNoReparsePoints(staging);
            if (!Directory.Exists(staging)) return false;
        }
        return true;
    }

    public async Task CleanupAsync(PackSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = GetSessionPath(snapshot);
            FilePackPaths.EnsureNoReparsePoints(session);
            if (!Directory.Exists(session)) return;
            bool owned;
            try { owned = await ValidateOwnerAsync(snapshot, requireStaging: false, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (IsOwnershipError(exception))
            { throw new IOException("工作区归属无法验证，已阻止清理。", exception); }
            if (!owned) throw new IOException("工作区归属标记不匹配，已阻止清理。");
            await Task.Run(() => DeleteOwnedTree(session, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private static void DeleteOwnedTree(string session, CancellationToken token)
    {
        // Validate the whole tree before deleting anything. Never use recursive deletion,
        // which could follow a link introduced by an external compressor.
        var marker = Path.Combine(session, MarkerName);
        FilePackPaths.EnsureNoReparsePoints(marker);
        if (new FileInfo(marker).Length > 4096) throw new IOException("工作区归属标记无效。");
        var markerBytes = File.ReadAllBytes(marker);
        var pending = new Stack<string>(); var directories = new List<string>(); var files = new List<string>(); pending.Push(session);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested(); FilePackPaths.EnsureNoReparsePoints(directory); directories.Add(directory);
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (!FilePackPaths.IsWithin(child, session)) throw new IOException("清理路径超出工作区。");
                FilePackPaths.EnsureNoReparsePoints(child);
                if ((File.GetAttributes(child) & FileAttributes.Directory) != 0) pending.Push(child);
                else if (!string.Equals(child, marker, StringComparison.OrdinalIgnoreCase)) files.Add(child);
            }
        }
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested(); FilePackPaths.EnsureNoReparsePoints(file); File.Delete(file);
        }
        for (var index = directories.Count - 1; index > 0; index--)
        {
            token.ThrowIfCancellationRequested(); FilePackPaths.EnsureNoReparsePoints(directories[index]); Directory.Delete(directories[index], recursive: false);
        }
        token.ThrowIfCancellationRequested(); FilePackPaths.EnsureNoReparsePoints(marker);
        if (!File.ReadAllBytes(marker).AsSpan().SequenceEqual(markerBytes)) throw new IOException("清理期间工作区归属标记发生变化。");
        File.Delete(marker);
        try { FilePackPaths.EnsureNoReparsePoints(session); Directory.Delete(session, recursive: false); }
        catch
        {
            // Preserve ownership when an external process added a file during cleanup.
            FilePackPaths.EnsureNoReparsePoints(session);
            if (Directory.Exists(session) && !File.Exists(marker)) File.WriteAllBytes(marker, markerBytes);
            throw;
        }
    }

    private static bool IsOwnershipError(Exception exception) => exception is IOException or UnauthorizedAccessException or
        ArgumentException or JsonException or NotSupportedException or System.Security.SecurityException;
    private sealed record OwnerMarker(int Version, Guid TaskId, Guid SessionId);
}
