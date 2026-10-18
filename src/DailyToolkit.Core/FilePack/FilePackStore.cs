using System.Text.Json;

namespace DailyToolkit.Core.FilePack;

public sealed class FilePackStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<Guid> _unreadableTasks = [];
    private readonly List<string> _diagnostics = [];
    private bool _unreadablePreferences;
    private bool _unreadableDraft;
    public string RootDirectory { get; }
    public IReadOnlyList<string> Diagnostics { get { lock (_diagnostics) return _diagnostics.ToArray(); } }
    private string TasksDirectory => Path.Combine(RootDirectory, "Tasks");
    private string PreferencesPath => Path.Combine(RootDirectory, "preferences.json");
    private string DraftPath => Path.Combine(RootDirectory, "draft.json");

    public FilePackStore(string rootDirectory) => RootDirectory = Path.GetFullPath(rootDirectory);

    public async Task<IReadOnlyList<PackTaskInfo>> LoadTasksAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FilePackPaths.EnsureNoReparsePoints(TasksDirectory);
            if (!Directory.Exists(TasksDirectory)) return [];
            var tasks = new List<PackTaskInfo>();
            foreach (var path in Directory.EnumerateFiles(TasksDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
                { Diagnose("发现无法识别的任务文件，已保留：" + Path.GetFileName(path)); continue; }
                try
                {
                    FilePackPaths.EnsureNoReparsePoints(path);
                    if (new FileInfo(path).Length > 64L * 1024 * 1024) throw new InvalidDataException("任务文件过大。");
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                    var task = await JsonSerializer.DeserializeAsync<PackTaskInfo>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                    if (task?.Snapshot is null || task.TaskId != id || task.TaskId == Guid.Empty || task.Snapshot.SessionId == Guid.Empty ||
                        task.Snapshot.Options is null || task.Snapshot.Entries is null || !Enum.IsDefined(task.State) ||
                        task.Snapshot.Entries.Count > FilePackPaths.MaximumEntries ||
                        task.Snapshot.Entries.Any(entry => entry is null || entry.SourcePath is null || entry.ArchivePath is null || entry.SourceRoot is null) ||
                        task.OutputPath is null || task.WorkspacePath is null || task.Snapshot.Options.OutputDirectory is null || task.Snapshot.Options.ArchiveName is null)
                        throw new InvalidDataException("任务文件内容无效。");
                    if (!Enum.IsDefined(task.Snapshot.Options.Mode) || !Enum.IsDefined(task.Snapshot.Options.Format) ||
                        !Enum.IsDefined(task.Snapshot.Options.Compression) || !Enum.IsDefined(task.Snapshot.Options.OutputPolicy) ||
                        !double.IsFinite(task.Percent) || task.Snapshot.Entries.Any(entry => !entry.IsDirectory &&
                            (entry.SourceStamp is null || entry.SourceStamp.Length != entry.Size)))
                        throw new InvalidDataException("任务选项或来源信息无效。");
                    FilePackPaths.ValidateEntries(task.Snapshot.Entries);
                    tasks.Add(task);
                }
                catch (Exception exception) when (IsStorageError(exception) || exception is ArgumentException)
                { _unreadableTasks.Add(id); Diagnose("任务未能读取，原文件保留：" + Path.GetFileName(path)); }
            }
            return tasks.OrderBy(task => task.QueueOrder).ThenBy(task => task.Snapshot.CreatedAt).ToArray();
        }
        finally { _gate.Release(); }
    }

    public async Task SaveTaskAsync(PackTaskInfo task, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_unreadableTasks.Contains(task.TaskId)) throw new IOException("原任务文件无法读取，已阻止覆盖。");
            await WriteAsync(Path.Combine(TasksDirectory, task.TaskId.ToString("N") + ".json"), task, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RemoveTaskAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_unreadableTasks.Contains(taskId)) throw new IOException("原任务文件无法读取，已保留原文件。");
            var path = Path.Combine(TasksDirectory, taskId.ToString("N") + ".json");
            FilePackPaths.EnsureNoReparsePoints(path);
            if (File.Exists(path)) File.Delete(path);
        }
        finally { _gate.Release(); }
    }

    public async Task<FilePackPreferences> LoadPreferencesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                FilePackPaths.EnsureNoReparsePoints(PreferencesPath);
                if (!File.Exists(PreferencesPath)) return new();
                if (new FileInfo(PreferencesPath).Length > 16384) throw new InvalidDataException("设置文件过大。");
                await using var stream = new FileStream(PreferencesPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                return (await JsonSerializer.DeserializeAsync<FilePackPreferences>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("设置文件为空。")).Normalize();
            }
            catch (Exception exception) when (IsStorageError(exception) || exception is ArgumentException)
            { _unreadablePreferences = true; Diagnose("文件集装箱设置未能读取，原文件保留，暂时不能覆盖。"); return new(); }
        }
        finally { _gate.Release(); }
    }

    public async Task SavePreferencesAsync(FilePackPreferences preferences, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_unreadablePreferences) throw new IOException("原设置文件无法读取，已阻止覆盖。");
            await WriteAsync(PreferencesPath, preferences.Normalize(), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<PackDraft?> LoadDraftAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FilePackPaths.EnsureNoReparsePoints(DraftPath);
            if (!File.Exists(DraftPath)) return null;
            try
            {
                if (new FileInfo(DraftPath).Length > 64L * 1024 * 1024) throw new InvalidDataException("草稿清单过大。");
                await using var stream = new FileStream(DraftPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                var draft = await JsonSerializer.DeserializeAsync<PackDraft>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("草稿清单为空。");
                ValidateDraft(draft);
                return draft;
            }
            catch (Exception exception) when (IsStorageError(exception) || exception is ArgumentException)
            {
                _unreadableDraft = true;
                Diagnose("待打包清单未能读取，原文件保留，暂时不能覆盖。");
                throw new IOException("待打包清单未能读取，原文件已保留。", exception);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task SaveDraftAsync(PackDraft draft, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_unreadableDraft) throw new IOException("原待打包清单无法读取，已阻止覆盖。");
            ValidateDraft(draft);
            await WriteAsync(DraftPath, draft, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static void ValidateDraft(PackDraft draft)
    {
        if (draft.Entries is null || draft.Entries.Count > FilePackPaths.MaximumEntries ||
            draft.TaskName is null || draft.ArchiveName is null || draft.OutputDirectory is null ||
            draft.NewFolderPath is null || draft.MoveTargetPath is null || !Enum.IsDefined(draft.Mode) ||
            !Enum.IsDefined(draft.Format) || !Enum.IsDefined(draft.Compression) ||
            draft.Entries.Any(row => row?.Entry is null || row.PathDraft is null || row.Entry.SourcePath is null ||
                row.Entry.ArchivePath is null || row.Entry.SourceRoot is null || row.Entry.Size < 0 ||
                !row.Entry.IsDirectory && (row.Entry.SourceStamp is null || row.Entry.SourceStamp.Length != row.Entry.Size)))
            throw new InvalidDataException("待打包清单内容无效。");
        FilePackPaths.ValidateEntries(draft.Entries.Select(row => row.Entry));
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        FilePackPaths.EnsureNoReparsePoints(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FilePackPaths.EnsureNoReparsePoints(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                // A queued snapshot leaves room for later progress/errors and paths.
                // Never write a task that this repository would reject on restart.
                var limit = value is FilePackPreferences ? 16384 :
                    value is PackTaskInfo { State: PackState.Queued } ? 63L * 1024 * 1024 : 64L * 1024 * 1024;
                var bounded = new BoundedMetadataStream(stream, limit, value is FilePackPreferences
                    ? "设置内容过长，请缩短输出路径。" : "任务清单过大，请拆分任务后重试。");
                await JsonSerializer.SerializeAsync(bounded, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            FilePackPaths.EnsureNoReparsePoints(path);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Diagnose(string message) { lock (_diagnostics) { if (!_diagnostics.Contains(message)) _diagnostics.Add(message); } }
    private sealed class BoundedMetadataStream(Stream target, long limit, string error) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => target.Length;
        public override long Position { get => target.Position; set => throw new NotSupportedException(); }
        private void Check(int length)
        { if (length > limit - target.Position) throw new InvalidDataException(error); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); target.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); target.Write(buffer); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { Check(buffer.Length); return target.WriteAsync(buffer, token); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => WriteAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() => target.Flush();
        public override Task FlushAsync(CancellationToken token) => target.FlushAsync(token);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
    internal static bool IsStorageError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or JsonException or System.Security.SecurityException or NotSupportedException;
}
