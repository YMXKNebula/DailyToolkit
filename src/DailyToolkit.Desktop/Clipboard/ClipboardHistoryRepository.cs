using System.Security.Cryptography;
using System.Text.Json;
using DailyToolkit.Core.Clipboard;
using Microsoft.Data.Sqlite;

namespace DailyToolkit.Desktop.ClipRecall;

// Each operation owns its connection, runs on the pool and is serialized by the
// gate. SQLite's "async" API is synchronous, so it is deliberately not used on UI.
public sealed partial class ClipboardHistoryRepository : IAsyncDisposable
{
    private const string Columns = "id,payload,created,captured,used,saved,pinned,manual,bytes";
    private readonly SemaphoreSlim _gate = new(1);
    private readonly IClipboardDataProtection _protection;
    private readonly TimeProvider _clock;
    private byte[]? _hashKey;
    private bool _disposed;
    private sealed record Payload(ClipboardContent Content, string Note, string? SourceApplication);
    public string DirectoryPath { get; }
    public string DatabasePath => Path.Combine(DirectoryPath, "history.db");
    public string BackupPath => Path.Combine(DirectoryPath, "history.backup.db");
    public bool IsReady { get; private set; }
    public event Action? Changed;

    public ClipboardHistoryRepository(string directory) : this(directory, new ClipboardDataProtection(), TimeProvider.System) { }
    internal ClipboardHistoryRepository(string directory, IClipboardDataProtection protection, TimeProvider clock)
    { DirectoryPath = Path.GetFullPath(directory); _protection = protection; _clock = clock; }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false, DefaultTimeout = 1 }.ToString());
        try { connection.Open(); Execute(connection, "PRAGMA secure_delete=ON; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;"); return connection; }
        catch { connection.Dispose(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static int Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    { using var command = Command(connection, sql, parameters); return command.ExecuteNonQuery(); }
    private async Task<T> RunAsync<T>(Func<SqliteConnection, T> operation, CancellationToken token = default, bool requireReady = true)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (requireReady && !IsReady) throw new InvalidOperationException("历史数据库尚不可用，原数据已保留。");
            return await Task.Run(() => { token.ThrowIfCancellationRequested(); using var connection = Open(); return operation(connection); }, token).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public async Task InitializeAsync()
    {
        await Task.Run(() =>
        {
            Directory.CreateDirectory(DirectoryPath);
            // A crash during the multi-file restore can temporarily leave the
            // main database absent. Never hide recoverable history with an empty
            // replacement. No original/recovery file is changed by this check.
            if (!File.Exists(DatabasePath) && (File.Exists(BackupPath) || RecoveryTemporaryFiles().Any() ||
                RecoveryDirectories().Any(directory => File.Exists(Path.Combine(directory, "history.db")))))
                throw new InvalidDataException("主历史数据库缺失，可能存在未完成的恢复；已保留备份和恢复文件。请恢复最近备份或检查数据目录。");
        }).ConfigureAwait(false);
        await RunAsync(connection =>
        {
            using (var check = Command(connection, "PRAGMA quick_check;"))
                if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                    throw new InvalidDataException("历史数据库校验失败，原文件已保留；可恢复最近备份。");
            using var versionCommand = Command(connection, "PRAGMA user_version;");
            var version = Convert.ToInt32(versionCommand.ExecuteScalar());
            if (version > 3) throw new InvalidDataException("历史数据库来自更新版本，请使用相应版本打开。");
            Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=256;");
            using var transaction = connection.BeginTransaction();
            // v0 -> v1 -> v2 is atomic. Images are separately encrypted BLOBs,
            // so list/search/note operations never decode full image payloads.
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS metadata(name TEXT PRIMARY KEY, value BLOB NOT NULL);
                CREATE TABLE IF NOT EXISTS records(
                    id TEXT PRIMARY KEY, type INTEGER NOT NULL, hash BLOB NOT NULL, payload BLOB NOT NULL,
                    created INTEGER NOT NULL, captured INTEGER, used INTEGER,
                    saved INTEGER NOT NULL DEFAULT 0, pinned INTEGER NOT NULL DEFAULT 0,
                    manual INTEGER NOT NULL DEFAULT 0, bytes INTEGER NOT NULL,
                    deleted_group TEXT, deleted_until INTEGER);
                CREATE INDEX IF NOT EXISTS record_hash ON records(type,hash) WHERE deleted_group IS NULL;
                CREATE INDEX IF NOT EXISTS record_order ON records(pinned DESC, captured DESC, created DESC) WHERE deleted_group IS NULL;
                """);
            if (version < 2)
                Execute(connection, "ALTER TABLE records ADD COLUMN image_payload BLOB; ALTER TABLE records ADD COLUMN thumbnail BLOB;");
            InitializeFileCache(connection);
            Execute(connection, "PRAGMA user_version=3;");
            using var keyCommand = Command(connection, "SELECT value FROM metadata WHERE name='hash-key';");
            if (keyCommand.ExecuteScalar() is byte[] key) _hashKey = _protection.Unprotect(key);
            else
            {
                _hashKey = RandomNumberGenerator.GetBytes(32);
                Execute(connection, "INSERT INTO metadata(name,value) VALUES('hash-key',$key);", ("$key", _protection.Protect(_hashKey)));
            }
            if (_hashKey.Length != 32) throw new InvalidDataException("历史密钥无效，原文件已保留。");
            transaction.Commit(); IsReady = true; return true;
        }, requireReady: false).ConfigureAwait(false);
    }
    private byte[] Fingerprint(ClipboardContent content)
    {
        var bytes = content.CanonicalBytes();
        try { return HMACSHA256.HashData(_hashKey!, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private byte[] Encode(ClipboardContent content, string note, string? source)
    {
        if (content.Type == ClipboardContentType.Image) content = content with { Binary = null };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Payload(content, note, source));
        try { return _protection.Protect(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private ClipboardHistoryRecord Decode(SqliteDataReader reader)
    {
        var bytes = _protection.Unprotect((byte[])reader[1]);
        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(bytes) ?? throw new InvalidDataException("历史数据无效。");
            return new(Guid.Parse(reader.GetString(0)), payload.Content, DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                reader.GetBoolean(5), reader.GetBoolean(6), payload.Note, reader.GetBoolean(7), payload.SourceApplication, reader.GetInt64(8));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    private ClipboardContent ReadImage(SqliteConnection connection, Guid id, ClipboardContent metadata)
    {
        using var command = Command(connection, "SELECT image_payload FROM records WHERE id=$id;", ("$id", id.ToString()));
        if (command.ExecuteScalar() is not byte[] encrypted) throw new InvalidDataException("图片正文缺失，原记录保留。");
        var png = _protection.Unprotect(encrypted);
        if (png.LongLength != metadata.ImageEncodedBytes) throw new InvalidDataException("图片正文大小不一致，原记录保留。");
        return metadata with { Binary = png };
    }
    private ClipboardHistoryRecord? ReadRecord(SqliteConnection connection, Guid id, bool withImage = true)
    {
        using var command = Command(connection, $"SELECT {Columns} FROM records WHERE id=$id AND deleted_group IS NULL;", ("$id", id.ToString()));
        ClipboardHistoryRecord? record;
        using (var reader = command.ExecuteReader()) record = reader.Read() ? Decode(reader) : null;
        return withImage && record?.Content.Type == ClipboardContentType.Image ? record with { Content = ReadImage(connection, id, record.Content) } : record;
    }
    public Task<ClipboardHistoryRecord?> GetAsync(Guid id, CancellationToken token = default) => RunAsync(connection => ReadRecord(connection, id), token);
    internal Task<ClipboardHistoryRecord?> GetSummaryAsync(Guid id, CancellationToken token = default) =>
        RunAsync(connection => ReadRecord(connection, id, withImage: false), token);
    internal Task<byte[]?> GetThumbnailAsync(Guid id, CancellationToken token = default) => RunAsync(connection =>
    {
        using var command = Command(connection, "SELECT thumbnail FROM records WHERE id=$id AND deleted_group IS NULL;", ("$id", id.ToString()));
        return command.ExecuteScalar() is byte[] encrypted ? _protection.Unprotect(encrypted) : null;
    }, token);
    private static long ImageStorageBytes(SqliteConnection connection, Guid id)
    {
        using var command = Command(connection, "SELECT COALESCE(LENGTH(image_payload),0)+COALESCE(LENGTH(thumbnail),0) FROM records WHERE id=$id;", ("$id", id.ToString()));
        return Convert.ToInt64(command.ExecuteScalar() ?? 0);
    }
    private ClipboardHistoryRecord? FindMatch(SqliteConnection connection, ClipboardContent content, Guid? except = null)
    {
        using var command = Command(connection, $"SELECT {Columns} FROM records WHERE type=$type AND hash=$hash AND deleted_group IS NULL;",
            ("$type", (int)content.Type), ("$hash", Fingerprint(content)));
        var candidates = new List<ClipboardHistoryRecord>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) candidates.Add(Decode(reader));
        foreach (var candidate in candidates)
        {
            if (candidate.Id == except) continue;
            var record = candidate.Content.Type == ClipboardContentType.Image ? candidate with { Content = ReadImage(connection, candidate.Id, candidate.Content) } : candidate;
            if (content.Type == ClipboardContentType.Image ? ClipboardImageCodec.SamePixels(record.Content, content) : record.Content.SameContent(content)) return record;
        }
        return null;
    }
    private static void ValidateContent(ClipboardContent content, ClipboardHistorySettings settings)
    {
        if (content.Type == ClipboardContentType.Image)
        {
            if (content.Binary is not { Length: > 0 } || content.BinaryFormat != "png" || content.ImageFingerprint is not { Length: 64 })
                throw new InvalidDataException("图片内容无效，未记录。");
            if (content.ContentBytes > settings.MaximumImageMegabytes * 1024L * 1024) throw new ClipboardImageLimitException();
            ClipboardImageCodec.ValidateDimensions(content.ImageWidth, content.ImageHeight, ClipboardImageCodec.PixelLimit(settings.MaximumImageMegabytes));
            return;
        }
        if (content.Type is not (ClipboardContentType.Text or ClipboardContentType.Link or ClipboardContentType.Files) ||
            content.ContentBytes == 0 || content.ContentBytes > settings.MaximumTextKilobytes * 1024L)
            throw new InvalidDataException("内容为空、不支持或超过单条大小限制，未记录。");
    }
    private void EnsureSpace(long bytes)
    {
        var root = Path.GetPathRoot(DirectoryPath);
        if (root is not null && new DriveInfo(root).AvailableFreeSpace < Math.Max(8 * 1024 * 1024, bytes + 1024 * 1024))
            throw new IOException("磁盘剩余空间不足，已停止写入历史。");
    }
    private static void ValidateOrdinarySize(long bytes, bool saved, ClipboardHistorySettings settings)
    {
        if (!saved && bytes > settings.MaximumOrdinaryMegabytes * 1024L * 1024)
            throw new InvalidDataException("该记录的加密存储大小超过普通历史总容量，未写入。请提高容量限制。");
    }
    private void InvalidateBackup()
    { if (File.Exists(BackupPath)) File.Delete(BackupPath); }
    private void Insert(SqliteConnection connection, ClipboardHistoryRecord record, ClipboardHistorySettings settings)
    {
        var payload = Encode(record.Content, record.Note, record.SourceApplication);
        byte[]? image = null, thumbnail = null;
        if (record.Content.Type == ClipboardContentType.Image)
        {
            image = _protection.Protect(record.Content.Binary!);
            var small = ClipboardImageCodec.Thumbnail(record.Content);
            try { thumbnail = _protection.Protect(small); } finally { CryptographicOperations.ZeroMemory(small); }
        }
        var bytes = payload.LongLength + (image?.LongLength ?? 0) + (thumbnail?.LongLength ?? 0) + 128;
        EnsureSpace(bytes); ValidateOrdinarySize(bytes, record.IsSaved, settings);
        Execute(connection, """
            INSERT INTO records(id,type,hash,payload,created,captured,used,saved,pinned,manual,bytes,image_payload,thumbnail)
            VALUES($id,$type,$hash,$payload,$created,$captured,$used,$saved,$pinned,$manual,$bytes,$image,$thumbnail);
            """, ("$id", record.Id.ToString()), ("$type", (int)record.Content.Type), ("$hash", Fingerprint(record.Content)),
            ("$payload", payload), ("$created", record.CreatedAt.ToUnixTimeMilliseconds()),
            ("$captured", record.LastCapturedAt?.ToUnixTimeMilliseconds()), ("$used", record.LastUsedAt?.ToUnixTimeMilliseconds()),
            ("$saved", record.IsSaved), ("$pinned", record.IsPinned), ("$manual", record.IsManual), ("$bytes", bytes),
            ("$image", image), ("$thumbnail", thumbnail));
    }
    public async Task<ClipboardHistoryRecord> CaptureAsync(ClipboardContent content, DateTimeOffset at,
        string? source, ClipboardHistorySettings settings, bool manual = false)
    {
        ValidateContent(content, settings);
        if (content.Type == ClipboardContentType.Image)
            content = await Task.Run(() => ClipboardImageCodec.FromPng(content.Binary!, settings.MaximumImageMegabytes)).ConfigureAwait(false);
        var record = await RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var previous = FindMatch(connection, content);
            Guid id;
            if (previous is not null)
            {
                id = previous.Id;
                if (!manual)
                {
                    var payload = Encode(previous.Content, previous.Note, source ?? previous.SourceApplication);
                    var bytes = payload.LongLength + 128 + SupplementBytes(connection, previous.Id);
                    ValidateOrdinarySize(bytes, previous.IsSaved, settings); EnsureSpace(payload.Length);
                    Execute(connection, "UPDATE records SET captured=$at,manual=0,payload=$payload,bytes=$bytes WHERE id=$id;",
                        ("$at", Math.Max(at.ToUnixTimeMilliseconds(), previous.LastCapturedAt?.ToUnixTimeMilliseconds() ?? 0)),
                        ("$payload", payload), ("$bytes", bytes), ("$id", id.ToString()));
                }
            }
            else
            {
                id = Guid.NewGuid(); Insert(connection, new(id, content, at, manual ? null : at, null, false, false, "", manual, source, 0), settings);
            }
            var result = ReadRecord(connection, id, withImage: false)!;
            if (content.Type == ClipboardContentType.Image) result = result with { Content = content };
            Cleanup(connection, settings); transaction.Commit(); return result;
        }).ConfigureAwait(false);
        Changed?.Invoke(); return record;
    }
    public Task<ClipboardHistoryPage> QueryAsync(ClipboardHistoryQuery query, CancellationToken token = default) => RunAsync(connection =>
    {
        using var command = Command(connection, $"""
            SELECT {Columns} FROM records WHERE deleted_group IS NULL AND ($saved=0 OR saved=1) AND ($type=-1 OR type=$type)
            ORDER BY pinned DESC,COALESCE(captured,created) DESC,created DESC,id;
            """, ("$saved", query.SavedOnly), ("$type", query.Type is null ? -1 : (int)query.Type));
        using var reader = command.ExecuteReader(); var records = new List<ClipboardHistoryEntry>(); var total = 0;
        var limit = Math.Clamp(query.Limit, 1, 200); var offset = Math.Max(0, query.Offset); var search = query.Search.Trim();
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            // No plaintext preview, note, file path or FTS index is stored on disk.
            if (search.Length == 0)
            {
                if (total >= offset && records.Count < limit) records.Add(ClipboardHistoryEntry.FromRecord(Decode(reader)));
                total++;
            }
            else
            {
                var record = Decode(reader);
                if (!record.Content.Contains(search) && !record.Note.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                if (total >= offset && records.Count < limit) records.Add(ClipboardHistoryEntry.FromRecord(record)); total++;
            }
        }
        return new ClipboardHistoryPage(records, total);
    }, token);
    public async Task SetFlagsAsync(IEnumerable<Guid> ids, bool? saved, bool? pinned, ClipboardHistorySettings settings)
    {
        var selected = ids.Distinct().ToArray();
        await RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            foreach (var id in selected)
                Execute(connection, "UPDATE records SET saved=COALESCE($saved,saved),pinned=COALESCE($pinned,pinned) WHERE id=$id AND deleted_group IS NULL;",
                    ("$saved", saved), ("$pinned", pinned), ("$id", id.ToString()));
            Cleanup(connection, settings); transaction.Commit(); return true;
        }).ConfigureAwait(false); Changed?.Invoke();
    }
    public async Task UpdateNoteAsync(Guid id, string note, ClipboardHistorySettings? settings = null)
    {
        settings ??= new();
        if (note.Length > 16384) throw new InvalidDataException("备注最多 16384 个字符。");
        await RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var record = ReadRecord(connection, id, withImage: false) ?? throw new InvalidOperationException("记录已删除。");
            var payload = Encode(record.Content, note, record.SourceApplication); EnsureSpace(payload.Length);
            var bytes = payload.LongLength + 128 + SupplementBytes(connection, id);
            ValidateOrdinarySize(bytes, record.IsSaved, settings);
            Execute(connection, "UPDATE records SET payload=$payload,bytes=$bytes WHERE id=$id;",
                ("$payload", payload), ("$bytes", bytes), ("$id", id.ToString()));
            Cleanup(connection, settings); transaction.Commit(); return true;
        }).ConfigureAwait(false); Changed?.Invoke();
    }
    public async Task EditManualAsync(Guid id, ClipboardContent content, ClipboardHistorySettings settings)
    {
        ValidateContent(content, settings);
        await RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var record = ReadRecord(connection, id) ?? throw new InvalidOperationException("记录已删除。");
            if (!record.IsManual || record.Content.Type is not (ClipboardContentType.Text or ClipboardContentType.Link) ||
                content.Type is not (ClipboardContentType.Text or ClipboardContentType.Link))
                throw new InvalidOperationException("只有手动文本记录可以编辑；外部原始内容请复制为新记录。");
            if (FindMatch(connection, content, id) is not null) throw new InvalidOperationException("已有相同内容，请直接使用该记录。");
            var payload = Encode(content, record.Note, record.SourceApplication); EnsureSpace(payload.Length);
            ValidateOrdinarySize(payload.LongLength + 128, record.IsSaved, settings);
            Execute(connection, "UPDATE records SET type=$type,hash=$hash,payload=$payload,bytes=$bytes WHERE id=$id;",
                ("$type", (int)content.Type), ("$hash", Fingerprint(content)), ("$payload", payload),
                ("$bytes", payload.LongLength + 128), ("$id", id.ToString()));
            Cleanup(connection, settings); transaction.Commit(); return true;
        }).ConfigureAwait(false); Changed?.Invoke();
    }
    public Task MarkUsedAsync(Guid id) => RunAsync(connection => Execute(connection,
        "UPDATE records SET used=$at WHERE id=$id AND deleted_group IS NULL;", ("$at", _clock.GetUtcNow().ToUnixTimeMilliseconds()), ("$id", id.ToString())));
    public async Task<ClipboardDeleteReceipt> DeleteAsync(IEnumerable<Guid> ids)
    {
        var selected = ids.Distinct().ToArray(); var operation = Guid.NewGuid(); var expires = _clock.GetUtcNow().AddSeconds(12);
        var count = await RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(); var count = 0;
            foreach (var id in selected) count += Execute(connection,
                "UPDATE records SET deleted_group=$group,deleted_until=$until WHERE id=$id AND deleted_group IS NULL;",
                ("$group", operation.ToString()), ("$until", expires.ToUnixTimeMilliseconds()), ("$id", id.ToString()));
            if (count > 0) InvalidateBackup();
            transaction.Commit(); return count;
        }).ConfigureAwait(false); Changed?.Invoke();
        return new(operation, count, expires);
    }
    public async Task<int> UndoAsync(ClipboardDeleteReceipt receipt, ClipboardHistorySettings settings)
    {
        if (_clock.GetUtcNow() >= receipt.ExpiresAt) return 0;
        var count = await RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction(); var deleted = new List<ClipboardHistoryRecord>();
            using (var command = Command(connection, $"SELECT {Columns} FROM records WHERE deleted_group=$group AND deleted_until>$now;",
                ("$group", receipt.OperationId.ToString()), ("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds())))
            using (var reader = command.ExecuteReader()) while (reader.Read()) deleted.Add(Decode(reader));
            foreach (var record in deleted)
            {
                var content = record.Content.Type == ClipboardContentType.Image ? ReadImage(connection, record.Id, record.Content) : record.Content;
                var match = FindMatch(connection, content);
                if (match is null) Execute(connection, "UPDATE records SET deleted_group=NULL,deleted_until=NULL WHERE id=$id;", ("$id", record.Id.ToString()));
                else
                {
                    // A genuine external recapture during the undo window remains the
                    // current record. Merge annotations, never introduce a duplicate.
                    var payload = Encode(match.Content, match.Note.Length > 0 ? match.Note : record.Note, match.SourceApplication);
                    MergeFileCache(connection, record.Id, match.Id);
                    Execute(connection, "UPDATE records SET saved=$saved,pinned=$pinned,payload=$payload,bytes=$bytes WHERE id=$id;",
                        ("$saved", match.IsSaved || record.IsSaved), ("$pinned", match.IsPinned || record.IsPinned),
                        ("$payload", payload), ("$bytes", payload.LongLength + 128 + SupplementBytes(connection, match.Id)), ("$id", match.Id.ToString()));
                    Execute(connection, "DELETE FROM records WHERE id=$id;", ("$id", record.Id.ToString()));
                }
            }
            Cleanup(connection, settings); transaction.Commit(); return deleted.Count;
        }).ConfigureAwait(false); Changed?.Invoke(); return count;
    }
    public async Task ClearAsync(bool includeSaved)
    {
        try { await RunAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            InvalidateBackup();
            Execute(connection, includeSaved ? "DELETE FROM records;" : "DELETE FROM records WHERE saved=0;");
            transaction.Commit();
            try
            {
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE); VACUUM;");
                if (includeSaved)
                {
                    ClearRecoveryFiles();
                    foreach (var directory in RestoredDirectories()) DeleteRestoredDirectory(directory);
                }
            }
            catch (Exception exception) when (ClipboardCaptureProcessor.IsExpectedError(exception))
            {
                throw new InvalidOperationException("所选历史记录已清空，但存储压缩或恢复副本清理未完成；请检查数据目录并重试清空。", exception);
            }
            return true;
        }).ConfigureAwait(false); }
        finally { Changed?.Invoke(); }
    }
    private IEnumerable<string> RecoveryDirectories()
    {
        var root = Path.GetFullPath(DirectoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var path in Directory.EnumerateDirectories(DirectoryPath, "recovery-*"))
        {
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("恢复目录路径无效，请手动检查。");
            yield return full;
        }
    }
    private void ClearRecoveryFiles()
    {
        foreach (var directory in RecoveryDirectories())
        {
            foreach (var name in new[] { "history.db", "history.db-wal", "history.db-shm" })
            { var path = Path.Combine(directory, name); if (File.Exists(path)) File.Delete(path); }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
        foreach (var path in RecoveryTemporaryFiles()) File.Delete(path);
    }
    private IEnumerable<string> RecoveryTemporaryFiles()
    {
        const string prefix = "history.db.restore-";
        var root = Path.GetFullPath(DirectoryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, prefix + "*.tmp"))
        {
            var name = Path.GetFileName(path);
            if (!Guid.TryParseExact(name[prefix.Length..^4], "N", out _)) continue;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("恢复临时文件路径无效，请手动检查。");
            yield return full;
        }
    }
    private int Cleanup(SqliteConnection connection, ClipboardHistorySettings settings)
    {
        var now = _clock.GetUtcNow();
        var removed = Execute(connection, "DELETE FROM records WHERE deleted_until <= $now OR (saved=0 AND COALESCE(captured,created)<=$cutoff);",
            ("$now", now.ToUnixTimeMilliseconds()), ("$cutoff", now.AddDays(-settings.RetentionDays).ToUnixTimeMilliseconds()));
        var ordinary = new List<(string Id, long Bytes)>();
        using (var command = Command(connection, "SELECT id,bytes FROM records WHERE saved=0 AND deleted_group IS NULL ORDER BY COALESCE(captured,created) DESC,created DESC,id;"))
        using (var reader = command.ExecuteReader()) while (reader.Read()) ordinary.Add((reader.GetString(0), reader.GetInt64(1)));
        long bytes = 0; var count = 0;
        foreach (var record in ordinary)
        {
            bytes += record.Bytes; count++;
            if (count > settings.MaximumRecords || bytes > settings.MaximumOrdinaryMegabytes * 1024L * 1024)
                removed += Execute(connection, "DELETE FROM records WHERE id=$id;", ("$id", record.Id));
        }
        if (removed > 0) InvalidateBackup();
        return removed;
    }
    public async Task CleanupAsync(ClipboardHistorySettings settings)
    {
        var removed = await RunAsync(connection => { using var transaction = connection.BeginTransaction(); var count = Cleanup(connection, settings); transaction.Commit(); CleanupRestoredFiles(); return count; }).ConfigureAwait(false);
        if (removed > 0) Changed?.Invoke();
    }
    public Task<DateTimeOffset?> NextCleanupAsync(ClipboardHistorySettings settings) => RunAsync(connection =>
    {
        using var command = Command(connection, """
            SELECT MIN(due) FROM (
                SELECT MIN(COALESCE(captured,created))+$retention AS due FROM records WHERE saved=0
                UNION ALL SELECT MIN(deleted_until) FROM records WHERE deleted_until IS NOT NULL);
            """, ("$retention", (long)TimeSpan.FromDays(settings.RetentionDays).TotalMilliseconds));
        var value = command.ExecuteScalar(); var result = value is long due ? (DateTimeOffset?)DateTimeOffset.FromUnixTimeMilliseconds(due) : null;
        foreach (var directory in RestoredDirectories())
        {
            var marker = Path.Combine(directory, ".ready");
            var time = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : Directory.GetLastWriteTimeUtc(directory);
            var expiry = new DateTimeOffset(time, TimeSpan.Zero).AddDays(1);
            if (result is null || expiry < result) result = expiry;
        }
        return result;
    });
    public Task<ClipboardHistoryStatistics> StatisticsAsync() => RunAsync(connection =>
    {
        using var command = Command(connection, "SELECT COUNT(*),COALESCE(SUM(saved),0),COALESCE(SUM(CASE WHEN saved=0 THEN bytes ELSE 0 END),0) FROM records WHERE deleted_group IS NULL;");
        using var reader = command.ExecuteReader(); reader.Read();
        long bytes = 0;
        foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm", BackupPath })
            if (File.Exists(path)) bytes += new FileInfo(path).Length;
        foreach (var directory in RecoveryDirectories())
            foreach (var name in new[] { "history.db", "history.db-wal", "history.db-shm" })
            { var path = Path.Combine(directory, name); if (File.Exists(path)) bytes += new FileInfo(path).Length; }
        foreach (var path in RecoveryTemporaryFiles()) bytes += new FileInfo(path).Length;
        bytes += RestoredStorageBytes();
        var total = reader.GetInt32(0); var saved = reader.GetInt32(1);
        return new ClipboardHistoryStatistics(total, saved, total - saved, reader.GetInt64(2), bytes);
    });
    public async Task BackupAsync()
    {
        await RunAsync(connection =>
        {
            var temporary = BackupPath + ".tmp";
            try
            {
                EnsureSpace(new FileInfo(DatabasePath).Length + (File.Exists(DatabasePath + "-wal") ? new FileInfo(DatabasePath + "-wal").Length : 0));
                using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary, Pooling = false }.ToString()))
                { destination.Open(); connection.BackupDatabase(destination); }
                File.Move(temporary, BackupPath, true); return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }).ConfigureAwait(false);
    }
    public async Task RestoreBackupAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await Task.Run(() =>
            {
                using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = BackupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                {
                    backup.Open(); using var check = Command(backup, "PRAGMA quick_check;");
                    if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal)) throw new InvalidDataException("备份校验失败。");
                    using var version = Command(backup, "PRAGMA user_version;");
                    var backupVersion = Convert.ToInt32(version.ExecuteScalar());
                    if (backupVersion is not (1 or 2 or 3)) throw new InvalidDataException("备份版本不兼容。");
                    using var key = Command(backup, "SELECT value FROM metadata WHERE name='hash-key';");
                    if (key.ExecuteScalar() is not byte[] bytes) throw new InvalidDataException("备份密钥不可用。");
                    var clearKey = _protection.Unprotect(bytes);
                    try { if (clearKey.Length != 32) throw new InvalidDataException("备份密钥不可用。"); }
                    finally { CryptographicOperations.ZeroMemory(clearKey); }
                    using var records = Command(backup, $"SELECT {Columns} FROM records;");
                    var images = new List<ClipboardHistoryRecord>();
                    using (var reader = records.ExecuteReader()) while (reader.Read())
                    { var record = Decode(reader); if (record.Content.Type == ClipboardContentType.Image) images.Add(record); }
                    foreach (var record in images)
                    {
                        _ = ClipboardImageCodec.Decode(ReadImage(backup, record.Id, record.Content));
                        using var thumbnail = Command(backup, "SELECT thumbnail FROM records WHERE id=$id;", ("$id", record.Id.ToString()));
                        if (thumbnail.ExecuteScalar() is not byte[] encryptedThumbnail) throw new InvalidDataException("备份图片缩略图缺失。");
                        var small = _protection.Unprotect(encryptedThumbnail);
                        try { _ = ClipboardImageCodec.Preview(small, 160); } finally { CryptographicOperations.ZeroMemory(small); }
                    }
                    if (backupVersion >= 3) ValidateCachedFiles(backup);
                }
                EnsureSpace(new FileInfo(BackupPath).Length);
                var temporary = DatabasePath + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
                var recovery = Path.Combine(DirectoryPath, "recovery-" + Guid.NewGuid().ToString("N"));
                var moved = new List<(string Original, string Recovery)>();
                try
                {
                    // Finish the new copy before moving any original. A full disk
                    // therefore leaves the current database in its original place.
                    File.Copy(BackupPath, temporary); Directory.CreateDirectory(recovery);
                    foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
                        if (File.Exists(path))
                        { var destination = Path.Combine(recovery, Path.GetFileName(path)); File.Move(path, destination); moved.Add((path, destination)); }
                    File.Move(temporary, DatabasePath); IsReady = false;
                }
                catch
                {
                    foreach (var item in moved.AsEnumerable().Reverse()) if (!File.Exists(item.Original)) File.Move(item.Recovery, item.Original);
                    throw;
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        await InitializeAsync().ConfigureAwait(false); Changed?.Invoke();
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true; IsReady = false;
            if (_hashKey is not null) CryptographicOperations.ZeroMemory(_hashKey);
        }
        finally { _gate.Release(); }
    }
}
