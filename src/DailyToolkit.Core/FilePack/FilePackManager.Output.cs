using System.Text.Json;

namespace DailyToolkit.Core.FilePack;

public sealed partial class FilePackManager
{
    private sealed record OutputOwner(int Version, Guid TaskId, Guid SessionId);
    private string ReserveOutput(PackSnapshot snapshot)
    {
        var output = FilePackPaths.GetOutputPath(snapshot.Options);
        FilePackPaths.EnsureNoReparsePoints(output);
        if (snapshot.Options.OutputPolicy == PackOutputPolicy.AutoRename)
        {
            var directory = Path.GetDirectoryName(output)!; var stem = Path.GetFileNameWithoutExtension(output); var extension = Path.GetExtension(output);
            for (var index = 2; File.Exists(output) || Directory.Exists(output) || _outputs.ContainsKey(output); index++)
            {
                if (index > 10000) throw new IOException("无法生成未占用的压缩包名称。");
                output = Path.Combine(directory, stem + " (" + index + ")" + extension);
            }
        }
        else
        {
            if (_outputs.ContainsKey(output)) throw new IOException("另一个任务正在使用此输出文件名。");
            if (Directory.Exists(output)) throw new IOException("输出位置是文件夹，请更换压缩包名称。");
            if (File.Exists(output))
            {
                if (snapshot.Options.OutputPolicy != PackOutputPolicy.Overwrite || snapshot.Options.ApprovedOverwriteStamp is null)
                    throw new IOException("同名压缩包已存在，请选择覆盖、自动更名或其他位置。");
                if (FilePackPaths.CaptureSourceStamp(output) != snapshot.Options.ApprovedOverwriteStamp)
                    throw new IOException("准备覆盖的文件已经变化，请重新确认覆盖。");
            }
            else if (snapshot.Options.ApprovedOverwriteStamp is not null)
                throw new IOException("准备覆盖的文件已经消失，请重新确认输出位置。");
        }
        ProtectSources(snapshot, output);
        _outputs.Add(output, snapshot.TaskId); return output;
    }
    private static void ProtectSources(PackSnapshot snapshot, string output)
    {
        foreach (var entry in snapshot.Entries)
            if (entry.SourcePath.Length > 0 && (string.Equals(FilePackPaths.NormalizeSourcePath(entry.SourcePath), output, StringComparison.OrdinalIgnoreCase) ||
                !entry.IsDirectory && File.Exists(output) && FilePackPaths.AreSameFile(entry.SourcePath, output)))
                throw new IOException("压缩包不能覆盖任何原始文件或目录。");
    }
    private void ReleaseOutput(PackTaskInfo task) { lock (_sync) ReleaseOutputLocked(task); }
    private void ReleaseOutputLocked(PackTaskInfo task)
    { if (task.OutputPath.Length > 0 && _outputs.TryGetValue(task.OutputPath, out var owner) && owner == task.TaskId) _outputs.Remove(task.OutputPath); }
    private static string TemporaryDirectory(PackSnapshot snapshot) => Path.Combine(FilePackPaths.NormalizeSourcePath(snapshot.Options.OutputDirectory),
        ".dailytoolkit-filepack-" + snapshot.TaskId.ToString("N") + "-" + snapshot.SessionId.ToString("N"));
    private static async Task<string> PrepareTemporaryOutputAsync(PackTaskInfo task, CancellationToken cancellationToken)
    {
        var directory = TemporaryDirectory(task.Snapshot); var outputDirectory = Path.GetDirectoryName(directory)!;
        FilePackPaths.EnsureNoReparsePoints(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        FilePackPaths.EnsureAvailableSpace(outputDirectory, checked(task.Snapshot.Entries.Sum(entry => entry.Size) + 16L * 1024 * 1024));
        FilePackPaths.EnsureNoReparsePoints(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw new IOException("任务临时输出目录已存在，请先检查并清理。");
        Directory.CreateDirectory(directory);
        FilePackPaths.EnsureNoReparsePoints(directory);
        await using (var marker = new FileStream(Path.Combine(directory, ".filepack-output-owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
        {
            // Complete this small ownership record even when cancellation arrives.
            // Otherwise the owned empty directory would become impossible to clean safely.
            await JsonSerializer.SerializeAsync(marker, new OutputOwner(1, task.TaskId, task.Snapshot.SessionId)).ConfigureAwait(false);
            await marker.FlushAsync().ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Path.Combine(directory, "archive" + Path.GetExtension(task.OutputPath));
    }
    private static void CommitOutput(PackTaskInfo task, string temporaryOutput)
    {
        var snapshot = task.Snapshot;
        FilePackPaths.EnsureNoReparsePoints(task.OutputPath); FilePackPaths.EnsureNoReparsePoints(temporaryOutput);
        ProtectSources(snapshot, task.OutputPath);
        if (!File.Exists(temporaryOutput)) throw new IOException("校验后的临时压缩包不存在。");
        if (snapshot.Options.OutputPolicy == PackOutputPolicy.Overwrite && snapshot.Options.ApprovedOverwriteStamp is { } approved)
        {
            if (!File.Exists(task.OutputPath) || FilePackPaths.CaptureSourceStamp(task.OutputPath) != approved)
                throw new IOException("原压缩包在执行期间发生变化，已阻止覆盖。");
            File.Replace(temporaryOutput, task.OutputPath, null);
        }
        else
        {
            if (File.Exists(task.OutputPath) || Directory.Exists(task.OutputPath)) throw new IOException("输出位置已出现同名文件，已阻止覆盖。");
            File.Move(temporaryOutput, task.OutputPath, overwrite: false);
        }
    }
    private static async Task CleanupTemporaryOutputAsync(PackSnapshot snapshot)
    {
        if (snapshot.Options.Mode != PackMode.BuiltIn) return;
        var directory = TemporaryDirectory(snapshot); var root = FilePackPaths.NormalizeSourcePath(snapshot.Options.OutputDirectory);
        if (!Directory.Exists(directory)) return;
        if (!FilePackPaths.IsWithin(directory, root)) throw new IOException("临时输出目录超出任务输出位置，已阻止清理。");
        FilePackPaths.EnsureNoReparsePoints(directory);
        var markerPath = Path.Combine(directory, ".filepack-output-owner.json");
        FilePackPaths.EnsureNoReparsePoints(markerPath);
        if (!File.Exists(markerPath) || new FileInfo(markerPath).Length > 1024) throw new IOException("临时输出目录缺少有效归属标记。");
        await using var marker = new FileStream(markerPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        var owner = await JsonSerializer.DeserializeAsync<OutputOwner>(marker).ConfigureAwait(false);
        if (owner != new OutputOwner(1, snapshot.TaskId, snapshot.SessionId)) throw new IOException("临时输出目录归属不符，已阻止清理。");
        await marker.DisposeAsync().ConfigureAwait(false);
        DeleteOwnedDirectory(directory, root, markerPath);
    }
    private static void DeleteOwnedDirectory(string directory, string root, string markerPath)
    {
        if (!FilePackPaths.IsWithin(directory, root)) throw new IOException("临时目录越界，已阻止清理。");
        var markerBytes = File.ReadAllBytes(markerPath);
        var pending = new Stack<string>(); var directories = new List<string>(); var files = new List<string>(); pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            FilePackPaths.EnsureNoReparsePoints(current); directories.Add(current);
            foreach (var path in Directory.EnumerateFileSystemEntries(current))
            {
                FilePackPaths.EnsureNoReparsePoints(path);
                if (!FilePackPaths.IsWithin(path, directory)) throw new IOException("临时目录条目越界，已阻止清理。");
                if (Directory.Exists(path)) pending.Push(path);
                else if (!string.Equals(path, markerPath, StringComparison.OrdinalIgnoreCase)) files.Add(path);
            }
        }
        foreach (var path in files) { FilePackPaths.EnsureNoReparsePoints(path); File.Delete(path); }
        for (var index = directories.Count - 1; index > 0; index--)
        { FilePackPaths.EnsureNoReparsePoints(directories[index]); Directory.Delete(directories[index]); }
        FilePackPaths.EnsureNoReparsePoints(markerPath);
        if (!File.ReadAllBytes(markerPath).AsSpan().SequenceEqual(markerBytes)) throw new IOException("清理期间临时输出归属标记发生变化。");
        File.Delete(markerPath);
        try { FilePackPaths.EnsureNoReparsePoints(directory); Directory.Delete(directory); }
        catch
        {
            FilePackPaths.EnsureNoReparsePoints(directory);
            if (Directory.Exists(directory) && !File.Exists(markerPath)) File.WriteAllBytes(markerPath, markerBytes);
            throw;
        }
    }
}
