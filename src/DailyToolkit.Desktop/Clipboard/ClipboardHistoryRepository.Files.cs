using System.Security.Cryptography;
using System.Text.Json;
using DailyToolkit.Core.Clipboard;
using Microsoft.Data.Sqlite;

namespace DailyToolkit.Desktop.ClipRecall;

public sealed partial class ClipboardHistoryRepository
{
    private const int FileChunkBytes = 1024 * 1024, MaximumCacheEntries = 4096;
    private sealed record CachedFile(string RelativePath, bool Directory, long Length, string Hash = "");
    private sealed record FileManifest(Guid CacheId, string[] Roots, CachedFile[] Entries);
    private sealed record PlannedFile(string Source, CachedFile Entry, DateTime Modified);
    internal string RestoredFilesPath => Path.Combine(DirectoryPath, "RestoredFiles");
    private static void InitializeFileCache(SqliteConnection connection) => Execute(connection, """
        CREATE TABLE IF NOT EXISTS file_cache(
            record_id TEXT PRIMARY KEY REFERENCES records(id) ON DELETE CASCADE,
            manifest BLOB NOT NULL, bytes INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS file_chunks(
            record_id TEXT NOT NULL REFERENCES file_cache(record_id) ON DELETE CASCADE ON UPDATE CASCADE,
            file_index INTEGER NOT NULL, chunk_index INTEGER NOT NULL, data BLOB NOT NULL,
            PRIMARY KEY(record_id,file_index,chunk_index));
        """);
    private static long FileCacheBytes(SqliteConnection connection, Guid id)
    {
        using var command = Command(connection, "SELECT bytes FROM file_cache WHERE record_id=$id;", ("$id", id.ToString()));
        return Convert.ToInt64(command.ExecuteScalar() ?? 0);
    }
    private static long SupplementBytes(SqliteConnection connection, Guid id) =>
        ImageStorageBytes(connection, id) + FileCacheBytes(connection, id);
    public Task<bool> HasFileCacheAsync(Guid id) => RunAsync(connection => FileCacheBytes(connection, id) > 0);
    private static void MergeFileCache(SqliteConnection connection, Guid from, Guid to)
    {
        if (FileCacheBytes(connection, to) == 0)
            Execute(connection, "UPDATE file_cache SET record_id=$to WHERE record_id=$from;", ("$to", to.ToString()), ("$from", from.ToString()));
    }
    private static void CheckRegularPath(string path)
    {
        // Check every ancestor too: a junction in a parent must not hide recursion
        // or make the source/extraction escape its intended directory.
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("文件缓存暂不支持符号链接、连接点或云占位文件；原路径记录保留。");
    }
    private (string[] Roots, List<PlannedFile> Files) PlanFiles(string[] paths, long limit)
    {
        var entries = new List<PlannedFile>(); var roots = new List<string>(); long total = 0;
        void Add(string source, string relative, int depth = 0)
        {
            if (depth > 128) throw new InvalidDataException("文件目录层级过深；原路径记录保留。");
            if (entries.Count >= MaximumCacheEntries) throw new InvalidDataException("文件缓存最多包含 4096 个文件或目录；原路径记录保留。");
            CheckRegularPath(source);
            var directory = Directory.Exists(source);
            if (!directory && !File.Exists(source)) throw new InvalidDataException("原文件已不存在，无法建立缓存；原路径记录保留。");
            var length = directory ? 0 : new FileInfo(source).Length;
            total = checked(total + length);
            if (total > limit) throw new InvalidDataException("文件超过单条缓存大小限制；原路径记录保留。");
            entries.Add(new(source, new(relative, directory, length), File.GetLastWriteTimeUtc(source)));
            if (directory)
                foreach (var child in Directory.EnumerateFileSystemEntries(source)) Add(child, Path.Combine(relative, Path.GetFileName(child)), depth + 1);
        }
        for (var index = 0; index < paths.Length; index++)
        {
            var source = Path.GetFullPath(paths[index]);
            var volume = Path.GetPathRoot(source);
            if (source.StartsWith(@"\\", StringComparison.Ordinal) || volume is not null && new DriveInfo(volume).DriveType == DriveType.Network)
                throw new InvalidDataException("网络文件暂不自动缓存；原路径记录保留，仍可按路径复制。");
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(source));
            if (string.IsNullOrEmpty(name)) throw new InvalidDataException("暂不缓存整个磁盘根目录；原路径记录保留。");
            if (string.Equals(Path.TrimEndingDirectorySeparator(source), Path.TrimEndingDirectorySeparator(DirectoryPath), StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith(Path.TrimEndingDirectorySeparator(DirectoryPath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("不能再次缓存剪贴回溯自己的数据目录。");
            var relative = Path.Combine(index.ToString(System.Globalization.CultureInfo.InvariantCulture), name);
            roots.Add(relative); Add(source, relative);
        }
        return (roots.ToArray(), entries);
    }
    public async Task CacheFilesAsync(Guid id, ClipboardHistorySettings settings, CancellationToken token = default,
        Func<bool>? shouldContinue = null)
    {
        await RunAsync(connection =>
        {
            void CheckCancellation()
            { token.ThrowIfCancellationRequested(); if (shouldContinue?.Invoke() == false) throw new OperationCanceledException(); }
            CheckCancellation();
            var record = ReadRecord(connection, id, withImage: false) ?? throw new InvalidOperationException("记录已删除。");
            if (record.Content.Type != ClipboardContentType.Files) throw new InvalidOperationException("只有文件路径记录可以缓存。");
            var plan = PlanFiles(record.Content.Paths, settings.MaximumCachedFileMegabytes * 1024L * 1024);
            using var transaction = connection.BeginTransaction();
            var oldBytes = FileCacheBytes(connection, id);
            using var usage = Command(connection, "SELECT COALESCE(SUM(bytes),0) FROM file_cache;");
            var otherBytes = Convert.ToInt64(usage.ExecuteScalar()) - oldBytes;
            Execute(connection, "DELETE FROM file_cache WHERE record_id=$id;", ("$id", id.ToString()));
            Execute(connection, "INSERT INTO file_cache(record_id,manifest,bytes) VALUES($id,$manifest,0);", ("$id", id.ToString()), ("$manifest", Array.Empty<byte>()));
            var manifestEntries = new List<CachedFile>(); long bytes = 0; var buffer = new byte[FileChunkBytes];
            try
            {
                for (var fileIndex = 0; fileIndex < plan.Files.Count; fileIndex++)
                {
                    CheckCancellation(); var item = plan.Files[fileIndex];
                    if (item.Entry.Directory) { manifestEntries.Add(item.Entry); continue; }
                    CheckRegularPath(item.Source);
                    using var file = new FileStream(item.Source, FileMode.Open, FileAccess.Read, FileShare.Read, FileChunkBytes, FileOptions.SequentialScan);
                    if (file.Length != item.Entry.Length) throw new InvalidDataException("文件在缓存期间发生变化，缓存已撤销；原路径记录保留。");
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var chunk = 0; long readTotal = 0;
                    while (true)
                    {
                        CheckCancellation(); var read = file.Read(buffer, 0, buffer.Length); if (read == 0) break;
                        readTotal += read;
                        if (readTotal > item.Entry.Length) throw new InvalidDataException("文件在缓存期间发生变化，缓存已撤销。");
                        hash.AppendData(buffer, 0, read);
                        var clear = buffer.AsSpan(0, read).ToArray();
                        byte[] encrypted;
                        try { encrypted = _protection.Protect(clear); } finally { CryptographicOperations.ZeroMemory(clear); }
                        bytes += encrypted.LongLength + 64;
                        if (otherBytes + bytes > settings.MaximumFileCacheMegabytes * 1024L * 1024)
                            throw new InvalidDataException("文件缓存总容量已满；原路径记录保留。请清理缓存或提高限额。");
                        EnsureSpace(encrypted.Length * 3L);
                        Execute(connection, "INSERT INTO file_chunks(record_id,file_index,chunk_index,data) VALUES($id,$file,$chunk,$data);",
                            ("$id", id.ToString()), ("$file", fileIndex), ("$chunk", chunk++), ("$data", encrypted));
                    }
                    if (readTotal != item.Entry.Length || File.GetLastWriteTimeUtc(item.Source) != item.Modified)
                        throw new InvalidDataException("文件在缓存期间发生变化，缓存已撤销；原路径记录保留。");
                    manifestEntries.Add(item.Entry with { Hash = Convert.ToHexString(hash.GetHashAndReset()) });
                }
            }
            finally { CryptographicOperations.ZeroMemory(buffer); }
            CheckCancellation();
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new FileManifest(Guid.NewGuid(), plan.Roots, manifestEntries.ToArray()));
            byte[] protectedManifest;
            try { protectedManifest = _protection.Protect(manifest); } finally { CryptographicOperations.ZeroMemory(manifest); }
            bytes += protectedManifest.LongLength + 128;
            if (otherBytes + bytes > settings.MaximumFileCacheMegabytes * 1024L * 1024)
                throw new InvalidDataException("文件缓存总容量已满；原路径记录保留。");
            var recordBytes = record.StoredBytes - oldBytes + bytes;
            ValidateOrdinarySize(recordBytes, record.IsSaved, settings);
            Execute(connection, "UPDATE file_cache SET manifest=$manifest,bytes=$bytes WHERE record_id=$id;",
                ("$manifest", protectedManifest), ("$bytes", bytes), ("$id", id.ToString()));
            Execute(connection, "UPDATE records SET bytes=$bytes WHERE id=$id;", ("$bytes", recordBytes), ("$id", id.ToString()));
            Cleanup(connection, settings);
            if (ReadRecord(connection, id, withImage: false) is null)
                throw new InvalidOperationException("该记录会被普通历史容量清理，请先永久保存或提高普通历史容量。");
            InvalidateBackup(); transaction.Commit(); return true;
        }, token).ConfigureAwait(false);
        Changed?.Invoke();
    }
    public async Task RemoveFileCacheAsync(Guid id)
    {
        await RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(); var bytes = FileCacheBytes(connection, id);
            Execute(connection, "DELETE FROM file_cache WHERE record_id=$id;", ("$id", id.ToString()));
            Execute(connection, "UPDATE records SET bytes=bytes-$bytes WHERE id=$id;", ("$id", id.ToString()), ("$bytes", bytes));
            InvalidateBackup(); transaction.Commit(); return true;
        }).ConfigureAwait(false); Changed?.Invoke();
    }
    private FileManifest ReadManifest(SqliteConnection connection, Guid id)
    {
        using var command = Command(connection, "SELECT manifest FROM file_cache WHERE record_id=$id;", ("$id", id.ToString()));
        if (command.ExecuteScalar() is not byte[] encrypted) throw new InvalidOperationException("此记录还没有文件缓存，请先从仍存在的原文件建立缓存。");
        var clear = _protection.Unprotect(encrypted);
        try
        {
            var manifest = JsonSerializer.Deserialize<FileManifest>(clear) ?? throw new InvalidDataException("文件缓存清单无效。");
            if (manifest.CacheId == Guid.Empty || manifest.Entries.Length > MaximumCacheEntries || manifest.Roots.Length == 0)
                throw new InvalidDataException("文件缓存清单无效。");
            foreach (var entry in manifest.Entries)
            {
                _ = RestoredPath(Path.Combine(RestoredFilesPath, manifest.CacheId.ToString("N")), entry.RelativePath);
                if (entry.Length < 0 || !entry.Directory && entry.Hash.Length != 64) throw new InvalidDataException("文件缓存清单无效。");
            }
            foreach (var root in manifest.Roots)
                if (!manifest.Entries.Any(entry => entry.RelativePath == root)) throw new InvalidDataException("文件缓存根路径无效。");
            return manifest;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    private static string RestoredPath(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split(['\\', '/']).Length > 131 ||
            relative.Split(['\\', '/']).Any(part => part is "" or "." or ".." || part.Contains(':')))
            throw new InvalidDataException("文件缓存中的相对路径无效。");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件缓存路径越界。");
        return full;
    }
    // A stable materialized copy is reused while intact. Its contents must be
    // plaintext for other applications to read via CF_HDROP; encrypted originals
    // stay in SQLite. No global Temp directory or source directory is modified.
    public Task<string[]> MaterializeCachedFilesAsync(Guid id, ClipboardHistorySettings settings) => RunAsync(connection =>
    {
        if (ReadRecord(connection, id, withImage: false) is null) throw new InvalidOperationException("记录已删除。");
        var manifest = ReadManifest(connection, id);
        Directory.CreateDirectory(RestoredFilesPath); CheckRegularPath(RestoredFilesPath);
        var root = Path.Combine(RestoredFilesPath, manifest.CacheId.ToString("N"));
        var candidates = RestoredDirectories().Where(directory =>
        {
            var marker = Path.Combine(directory, ".ready");
            if (!File.Exists(marker)) return false;
            CheckRegularPath(marker);
            return new FileInfo(marker).Length == 32 && File.ReadAllText(marker) == manifest.CacheId.ToString("N");
        }).OrderByDescending(directory => File.GetLastWriteTimeUtc(Path.Combine(directory, ".ready"))).ToArray();
        foreach (var candidate in candidates)
        {
            root = candidate;
            CheckRegularPath(root);
            var intact = manifest.Entries.All(entry =>
            {
                var path = RestoredPath(root, entry.RelativePath);
                if (!File.Exists(path) && !Directory.Exists(path)) return false;
                CheckRegularPath(path);
                if (entry.Directory) return Directory.Exists(path);
                if (new FileInfo(path).Length != entry.Length) return false;
                using var file = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(file)) == entry.Hash;
            });
            if (intact) { File.SetLastWriteTimeUtc(Path.Combine(root, ".ready"), _clock.GetUtcNow().UtcDateTime); return manifest.Roots.Select(path => RestoredPath(root, path)).ToArray(); }
            // A consumer may have moved/edited a materialized file. Preserve that
            // folder until its expiry, and create a fresh complete copy.
        }
        if (Directory.Exists(root)) root = Path.Combine(RestoredFilesPath, Guid.NewGuid().ToString("N"));
        CleanupRestoredFiles();
        var length = manifest.Entries.Sum(entry => entry.Length);
        if (length + RestoredStorageBytes() > settings.MaximumFileCacheMegabytes * 1024L * 1024)
            throw new InvalidOperationException("临时文件夹容量已满，请等待过期清理或提高文件缓存总限额。");
        EnsureSpace(length + 1024 * 1024);
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < manifest.Entries.Length; index++)
            {
                var entry = manifest.Entries[index]; var path = RestoredPath(root, entry.RelativePath);
                if (entry.Directory) { Directory.CreateDirectory(path); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileChunkBytes, FileOptions.SequentialScan);
                WriteCachedFile(connection, id, index, entry, file); file.Flush(flushToDisk: true);
            }
            File.WriteAllText(Path.Combine(root, ".ready"), manifest.CacheId.ToString("N"));
            File.SetLastWriteTimeUtc(Path.Combine(root, ".ready"), _clock.GetUtcNow().UtcDateTime);
            return manifest.Roots.Select(path => RestoredPath(root, path)).ToArray();
        }
        catch { DeleteRestoredDirectory(root); throw; }
    });
    private void WriteCachedFile(SqliteConnection connection, Guid id, int index, CachedFile entry, Stream? destination)
    {
        using var command = Command(connection, "SELECT chunk_index,data FROM file_chunks WHERE record_id=$id AND file_index=$index ORDER BY chunk_index;",
            ("$id", id.ToString()), ("$index", index));
        using var reader = command.ExecuteReader(); using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0; var chunk = 0;
        while (reader.Read())
        {
            if (reader.GetInt32(0) != chunk++) throw new InvalidDataException("文件缓存分块缺失。");
            var clear = _protection.Unprotect((byte[])reader[1]);
            try
            {
                if (clear.Length > FileChunkBytes || length + clear.Length > entry.Length) throw new InvalidDataException("文件缓存大小不一致。");
                hash.AppendData(clear); destination?.Write(clear); length += clear.Length;
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        if (length != entry.Length || Convert.ToHexString(hash.GetHashAndReset()) != entry.Hash)
            throw new InvalidDataException("文件缓存校验失败，未生成可复制的文件。");
    }
    private void ValidateCachedFiles(SqliteConnection connection)
    {
        using var command = Command(connection, "SELECT record_id FROM file_cache;");
        var ids = new List<Guid>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        foreach (var id in ids)
        {
            var manifest = ReadManifest(connection, id);
            for (var index = 0; index < manifest.Entries.Length; index++)
                if (!manifest.Entries[index].Directory) WriteCachedFile(connection, id, index, manifest.Entries[index], null);
        }
    }
    private IEnumerable<string> RestoredDirectories()
    {
        if (!Directory.Exists(RestoredFilesPath)) yield break;
        CheckRegularPath(RestoredFilesPath);
        foreach (var directory in Directory.EnumerateDirectories(RestoredFilesPath))
            if (Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) yield return directory;
    }
    private void DeleteRestoredDirectory(string directory)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), Path.GetFullPath(RestoredFilesPath), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) throw new IOException("临时文件夹路径无效。");
        CheckRegularPath(directory);
        // Do not follow a junction inserted into the owned temporary directory.
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("临时文件夹包含链接，已保留以供检查。");
            if (Directory.Exists(path)) DeleteOwnedChild(path); else File.Delete(path);
        }
        Directory.Delete(directory);
        static void DeleteOwnedChild(string path)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("临时文件夹包含链接，已保留以供检查。");
                if (Directory.Exists(child)) DeleteOwnedChild(child); else File.Delete(child);
            }
            Directory.Delete(path);
        }
    }
    private void CleanupRestoredFiles()
    {
        foreach (var directory in RestoredDirectories())
        {
            var marker = Path.Combine(directory, ".ready");
            var time = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : Directory.GetLastWriteTimeUtc(directory);
            if (time > _clock.GetUtcNow().UtcDateTime.AddDays(-1)) continue;
            DeleteRestoredDirectory(directory);
        }
    }
    private long RestoredStorageBytes() => RestoredDirectories().Sum(directory =>
    {
        CheckRegularPath(directory);
        long sum = 0;
        void Walk(string path)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) throw new IOException("临时文件夹包含链接，未跟随读取。");
                if (Directory.Exists(child)) Walk(child); else sum += new FileInfo(child).Length;
            }
        }
        Walk(directory); return sum;
    });
}
