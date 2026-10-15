namespace DailyToolkit.Core.FilePack;

public sealed class PackPlanner
{
    public PackPlan Create(PackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.TaskId == Guid.Empty || snapshot.SessionId == Guid.Empty) throw new ArgumentException("任务或工作区标识无效。");
        if (!Enum.IsDefined(snapshot.Options.Mode) || !Enum.IsDefined(snapshot.Options.Format) || !Enum.IsDefined(snapshot.Options.Compression))
            throw new ArgumentException("打包选项无效。");
        if (snapshot.Entries.Count == 0) throw new ArgumentException("请先添加文件或包内文件夹。");
        FilePackPaths.ValidateEntries(snapshot.Entries);
        var bytes = 0L; var mapped = false;
        foreach (var entry in snapshot.Entries)
        {
            FilePackPaths.ValidateSource(entry);
            bytes = checked(bytes + entry.Size);
            if (entry.SourcePath.Length == 0) { mapped = true; continue; }
            var relative = Path.GetRelativePath(FilePackPaths.NormalizeSourcePath(entry.SourceRoot),
                FilePackPaths.NormalizeSourcePath(entry.SourcePath)).Replace('\\', '/');
            if (!string.Equals(relative, FilePackPaths.NormalizeArchivePath(entry.ArchivePath), StringComparison.Ordinal)) mapped = true;
        }
        var staging = snapshot.Options.Mode == PackMode.External || mapped;
        if (staging) return new([], true, bytes, snapshot.Options.Mode == PackMode.External
            ? "外部软件模式需要独立临时目录。" : "包内重命名或目录结构需要临时复制，以准确保留文件路径。");
        var batches = snapshot.Entries.GroupBy(entry => FilePackPaths.NormalizeSourcePath(entry.SourceRoot), StringComparer.OrdinalIgnoreCase)
            .Select(group => new PackBatch(group.Key, Array.AsReadOnly(group.ToArray()))).ToArray();
        return new(Array.AsReadOnly(batches), false, 0, "按来源目录分批直接读取原文件。");
    }
}
