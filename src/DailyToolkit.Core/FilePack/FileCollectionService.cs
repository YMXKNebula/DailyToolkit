namespace DailyToolkit.Core.FilePack;

public sealed class FileCollectionService
{
    public Task<PackCollectionResult> CollectAsync(IEnumerable<string> paths, IEnumerable<PackEntry>? existing = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var requested = paths.Take(FilePackPaths.MaximumEntries + 1).ToArray();
        var previous = (existing ?? []).Take(FilePackPaths.MaximumEntries + 1).ToArray();
        if (requested.Length > FilePackPaths.MaximumEntries || previous.Length > FilePackPaths.MaximumEntries)
            throw new InvalidOperationException($"一次最多收集 {FilePackPaths.MaximumEntries:N0} 个条目。");
        return Task.Run(() => Collect(requested, previous, cancellationToken), cancellationToken);
    }

    private static PackCollectionResult Collect(string[] paths, PackEntry[] existing, CancellationToken token)
    {
        FilePackPaths.ValidateEntries(existing);
        var entries = existing.ToList(); var warnings = new List<string>(); var omittedWarnings = 0;
        var sources = new HashSet<string>(existing.Where(entry => entry.SourcePath.Length > 0)
            .Select(entry => FilePackPaths.NormalizeSourcePath(entry.SourcePath)), StringComparer.OrdinalIgnoreCase);
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var occupied = new ArchiveNames(existing); var visited = existing.Length;
        void Warn(string warning) { if (warnings.Count < 200) warnings.Add(warning); else omittedWarnings++; }
        void Visit()
        {
            token.ThrowIfCancellationRequested();
            if (++visited > FilePackPaths.MaximumEntries)
                throw new InvalidOperationException($"一次最多检查 {FilePackPaths.MaximumEntries:N0} 个条目，请缩小收集范围。");
        }
        void AddFile(string source, string target, string sourceRoot)
        {
            if (!sources.Add(source)) { Warn($"已跳过重复来源：{source}"); return; }
            try
            {
                var stamp = FilePackPaths.CaptureSourceStamp(source);
                var unique = occupied.Choose(target, directory: false);
                if (unique != target) Warn($"包内重名已更名：{target} → {unique}");
                entries.Add(new(Guid.NewGuid(), source, unique, false, stamp.Length, stamp, sourceRoot));
                occupied.Reserve(unique, directory: false);
            }
            catch (Exception exception) when (IsCollectionError(exception)) { sources.Remove(source); Warn($"未能收集 {source}：{exception.Message}"); }
        }

        foreach (var path in paths)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var source = FilePackPaths.NormalizeSourcePath(path);
                if (!selected.Add(source)) { Warn($"已跳过重复来源：{source}"); continue; }
                Visit(); FilePackPaths.EnsureNoReparsePoints(source);
                var attributes = File.GetAttributes(source);
                var parent = Path.GetDirectoryName(source);
                if (string.IsNullOrEmpty(parent)) { Warn($"请添加具体文件或文件夹，不能添加磁盘根目录：{source}"); continue; }
                var name = FilePackPaths.NormalizeArchivePath(Path.GetFileName(source));
                if ((attributes & FileAttributes.Directory) == 0) { AddFile(source, name, parent); continue; }
                if (sources.Contains(source)) { Warn($"已跳过重复来源：{source}"); continue; }
                var archiveRoot = occupied.Choose(name, directory: true);
                var entriesBefore = entries.Count;
                occupied.Reserve(archiveRoot, directory: true);
                var pending = new Stack<(string Source, string Target)>(); pending.Push((source, archiveRoot));
                while (pending.TryPop(out var folder))
                {
                    token.ThrowIfCancellationRequested();
                    var hasChildren = false;
                    try
                    {
                        FilePackPaths.EnsureNoReparsePoints(folder.Source);
                        foreach (var child in Directory.EnumerateFileSystemEntries(folder.Source))
                        {
                            Visit();
                            try
                            {
                                FilePackPaths.EnsureNoReparsePoints(child);
                                var childAttributes = File.GetAttributes(child);
                                var childName = FilePackPaths.NormalizeArchivePath(Path.GetFileName(child));
                                var target = folder.Target + "/" + childName;
                                hasChildren = true;
                                if ((childAttributes & FileAttributes.Directory) == 0) AddFile(child, target, parent);
                                else
                                {
                                    if (sources.Contains(child)) { Warn($"已跳过重复来源：{child}"); continue; }
                                    var unique = occupied.Choose(target, directory: true);
                                    if (unique != target) Warn($"包内重名已更名：{target} → {unique}");
                                    occupied.Reserve(unique, directory: true); pending.Push((child, unique));
                                }
                            }
                            catch (Exception exception) when (IsCollectionError(exception)) { Warn($"已跳过 {child}：{exception.Message}"); }
                        }
                        if (!hasChildren && sources.Add(folder.Source))
                            entries.Add(new(Guid.NewGuid(), folder.Source, folder.Target, true, 0, null, parent));
                    }
                    catch (Exception exception) when (IsCollectionError(exception)) { Warn($"目录未能完整收集 {folder.Source}：{exception.Message}"); }
                }
                if (archiveRoot != name && entries.Count > entriesBefore) Warn($"包内重名目录已更名：{name} → {archiveRoot}");
            }
            catch (Exception exception) when (IsCollectionError(exception)) { Warn($"已跳过 {path}：{exception.Message}"); }
        }
        if (omittedWarnings > 0) warnings.Add($"另有 {omittedWarnings:N0} 条收集提示。");
        FilePackPaths.ValidateEntries(entries);
        return new(entries.AsReadOnly(), warnings.AsReadOnly());
    }

    private static bool IsCollectionError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private sealed class ArchiveNames
    {
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _nextSuffix = new(StringComparer.OrdinalIgnoreCase);
        public ArchiveNames(IEnumerable<PackEntry> entries)
        { foreach (var entry in entries) Reserve(FilePackPaths.NormalizeArchivePath(entry.ArchivePath), entry.IsDirectory); }
        public void Reserve(string path, bool directory)
        {
            if (directory) _directories.Add(path); else _files.Add(path);
            foreach (var parent in FilePackPaths.Ancestors(path)) _directories.Add(parent);
        }
        public string Choose(string path, bool directory)
        {
            path = FilePackPaths.NormalizeArchivePath(path);
            // A conflicting file can block an entire requested directory prefix.
            var blockedParent = FilePackPaths.Ancestors(path).LastOrDefault(_files.Contains);
            if (blockedParent is not null) return Choose(blockedParent, directory: true) + path[blockedParent.Length..];
            if (!_files.Contains(path) && !_directories.Contains(path)) return path;
            var slash = path.LastIndexOf('/'); var parent = slash < 0 ? "" : path[..(slash + 1)];
            var leaf = path[(slash + 1)..]; var extension = directory ? "" : Path.GetExtension(leaf);
            var stem = extension.Length == 0 ? leaf : leaf[..^extension.Length];
            var suffixKey = path + (directory ? "\0directory" : "\0file");
            var firstSuffix = _nextSuffix.GetValueOrDefault(suffixKey, 2);
            for (var suffix = firstSuffix; suffix <= FilePackPaths.MaximumEntries + 2; suffix++)
            {
                var extra = $" ({suffix})";
                var room = 255 - extra.Length - extension.Length;
                var renamedLeaf = room > 0 ? TrimName(stem, room) + extra + extension : TrimName(leaf, 255 - extra.Length) + extra;
                var candidate = parent + renamedLeaf;
                if (!_files.Contains(candidate) && !_directories.Contains(candidate))
                { _nextSuffix[suffixKey] = suffix + 1; return candidate; }
            }
            throw new InvalidOperationException("包内重名过多，请手动修改名称。");
        }
        private static string TrimName(string name, int limit)
        {
            var length = Math.Min(name.Length, limit);
            if (length > 0 && char.IsHighSurrogate(name[length - 1])) length--;
            return name[..length];
        }
    }
}
