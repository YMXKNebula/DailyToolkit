using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DailyToolkit.Core.FilePack;

public sealed class SevenZipAdapter(string executablePath)
{
    public string ExecutablePath { get; } = Path.GetFullPath(executablePath);
    public event Action<int>? ProcessStarted;
    public event Action<int>? ProcessExited;

    public async Task ExecuteAsync(PackSnapshot snapshot, PackPlan plan, string workspacePath, string temporaryOutputPath,
        int maximumParallel, IProgress<PackProgress>? progress, CancellationToken cancellationToken)
    {
        FilePackPaths.EnsureNoReparsePoints(ExecutablePath);
        if (!File.Exists(ExecutablePath)) throw new FileNotFoundException("内置 7-Zip 未找到，请重新安装程序。", ExecutablePath);
        FilePackPaths.ValidateEntries(snapshot.Entries);
        if (File.Exists(temporaryOutputPath) || Directory.Exists(temporaryOutputPath))
            throw new IOException("临时压缩包已存在，已停止以防止覆盖。");
        FilePackPaths.EnsureNoReparsePoints(temporaryOutputPath);
        foreach (var entry in snapshot.Entries) FilePackPaths.ValidateSource(entry);
        var batches = plan.RequiresStaging ? new[] { new PackBatch(workspacePath, snapshot.Entries) } : plan.Batches.ToArray();
        if (batches.Length == 0) throw new InvalidOperationException("打包计划没有文件。");
        var threads = Math.Clamp(System.Environment.ProcessorCount / Math.Clamp(maximumParallel, 1, 4), 1, 4);
        var totalBytes = snapshot.Entries.Sum(entry => entry.Size);
        long processedBytes = 0; var processedFiles = 0; var watch = Stopwatch.StartNew(); var lastProgress = -1000L;
        for (var index = 0; index < batches.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = batches[index];
            FilePackPaths.EnsureNoReparsePoints(batch.WorkingDirectory);
            foreach (var entry in batch.Entries)
            {
                FilePackPaths.ValidateSource(entry);
                if (plan.RequiresStaging)
                {
                    var stagedPath = Path.Combine(workspacePath, FilePackPaths.NormalizeArchivePath(entry.ArchivePath).Replace('/', Path.DirectorySeparatorChar));
                    FilePackPaths.EnsureNoReparsePoints(stagedPath);
                    if (entry.IsDirectory ? !Directory.Exists(stagedPath) : !File.Exists(stagedPath) || new FileInfo(stagedPath).Length != entry.Size)
                        throw new IOException("临时目录中的条目已改变：" + entry.ArchivePath);
                }
            }
            var listPath = Path.Combine(Path.GetDirectoryName(temporaryOutputPath)!, "list-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                await using (var stream = new FileStream(listPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    foreach (var entry in batch.Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = FilePackPaths.NormalizeArchivePath(entry.ArchivePath).Replace('/', Path.DirectorySeparatorChar);
                        await writer.WriteLineAsync(path.AsMemory(), cancellationToken).ConfigureAwait(false);
                    }
                }
                var arguments = new List<string> { "a", FormatSwitch(snapshot.Options.Format), "-y", "-spd", "-scsUTF-8", "-sccUTF-8", "-bb0", "-bsp1" };
                if (snapshot.Options.Format != PackFormat.Tar)
                {
                    arguments.Add("-mx=" + CompressionLevel(snapshot.Options.Compression));
                    arguments.Add("-mmt=" + threads.ToString(CultureInfo.InvariantCulture));
                    if (snapshot.Options.Format == PackFormat.Zip) { arguments.Add("-mm=Deflate"); arguments.Add("-mcu=on"); }
                    else if (snapshot.Options.Compression != PackCompression.Store)
                    {
                        var budget = (snapshot.Options.Compression == PackCompression.Maximum ? 32 : 16) / Math.Clamp(maximumParallel, 1, 4);
                        var dictionary = budget >= 32 ? 32 : budget >= 16 ? 16 : budget >= 8 ? 8 : 4;
                        arguments.Add("-m0=LZMA2"); arguments.Add("-md=" + dictionary + "m");
                    }
                }
                arguments.Add(temporaryOutputPath); arguments.Add("@" + listPath);
                var batchBytes = batch.Entries.Sum(entry => entry.Size); var batchIndex = index;
                await RunAsync(arguments, batch.WorkingDirectory, line =>
                {
                    var match = Regex.Match(line, @"(?:^|\s)(\d{1,3})%", RegexOptions.CultureInvariant);
                    if (!match.Success || watch.ElapsedMilliseconds - lastProgress < 200) return;
                    lastProgress = watch.ElapsedMilliseconds;
                    var fraction = Math.Clamp(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), 0, 100) / 100d;
                    var bytes = processedBytes + (long)(batchBytes * fraction);
                    var percent = totalBytes > 0 ? 90d * bytes / totalBytes : 90d * (batchIndex + fraction) / batches.Length;
                    progress?.Report(new(PackState.Compressing, percent, bytes, processedFiles));
                }, cancellationToken).ConfigureAwait(false);
                processedBytes += batchBytes; processedFiles += batch.Entries.Count(entry => !entry.IsDirectory);
                progress?.Report(new(PackState.Compressing, 90d * (index + 1) / batches.Length, processedBytes, processedFiles));
            }
            finally { if (File.Exists(listPath)) File.Delete(listPath); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new(PackState.Verifying, 92, processedBytes, processedFiles));
        await RunAsync(["t", "-y", "-sccUTF-8", "-bsp0", temporaryOutputPath], Path.GetDirectoryName(temporaryOutputPath)!, null, cancellationToken).ConfigureAwait(false);
        var verification = new ListingVerifier(snapshot.Entries);
        await RunAsync(["l", "-slt", "-ba", "-sccUTF-8", "-bsp0", temporaryOutputPath],
            Path.GetDirectoryName(temporaryOutputPath)!, verification.ReadLine, cancellationToken).ConfigureAwait(false);
        verification.Complete();
        foreach (var entry in snapshot.Entries) { cancellationToken.ThrowIfCancellationRequested(); FilePackPaths.ValidateSource(entry); }
        progress?.Report(new(PackState.Verifying, 99, processedBytes, processedFiles));
    }

    private static string FormatSwitch(PackFormat format) => format switch
    { PackFormat.Zip => "-tzip", PackFormat.SevenZip => "-t7z", PackFormat.Tar => "-ttar", _ => throw new ArgumentOutOfRangeException(nameof(format)) };
    private static int CompressionLevel(PackCompression compression) => compression switch
    { PackCompression.Store => 0, PackCompression.Fast => 1, PackCompression.Standard => 5, PackCompression.Maximum => 9, _ => throw new ArgumentOutOfRangeException(nameof(compression)) };

    private async Task RunAsync(IReadOnlyList<string> arguments, string workingDirectory, Action<string>? output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var job = FilePackProcessJob.Create();
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("7-Zip 未能启动。");
        try { job?.Assign(process); }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        Signal(ProcessStarted, process.Id);
        using var registration = cancellationToken.Register(() => Kill(process));
        var tail = new BoundedTail();
        var stdout = DrainAsync(process.StandardOutput, line => { tail.Add(line); output?.Invoke(line); }, cancellationToken);
        var stderr = DrainAsync(process.StandardError, tail.Add, cancellationToken);
        _ = stdout.ContinueWith(_ => Kill(process), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _ = stderr.ContinueWith(_ => Kill(process), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), stdout, stderr).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new IOException($"7-Zip 未完成（退出码 {process.ExitCode}）。" + tail.Value);
        }
        catch
        {
            Kill(process);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch (InvalidOperationException) { }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch (Exception exception) when (exception is OperationCanceledException or IOException or ArgumentException or InvalidDataException) { }
            throw;
        }
        finally { Signal(ProcessExited, process.Id); }
    }

    private static void Signal(Action<int>? handlers, int processId)
    {
        if (handlers is null) return;
        foreach (Action<int> handler in handlers.GetInvocationList())
            try { handler(processId); } catch (Exception exception) { Trace.WriteLine(exception); }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
    private static async Task DrainAsync(StreamReader reader, Action<string> received, CancellationToken cancellationToken)
    {
        var buffer = new char[4096]; var line = new StringBuilder(); var carriageReturn = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] == '\n' && carriageReturn) { carriageReturn = false; continue; }
                carriageReturn = buffer[index] == '\r';
                if (buffer[index] is '\r' or '\n') { received(line.ToString()); line.Clear(); }
                else if (line.Length < 65536) line.Append(buffer[index]);
                else throw new InvalidDataException("7-Zip 输出行过长。");
            }
        }
        if (line.Length > 0) received(line.ToString());
    }
    private sealed class BoundedTail
    {
        private readonly StringBuilder _text = new();
        public void Add(string line)
        {
            lock (_text) { _text.AppendLine(line); if (_text.Length > 8192) _text.Remove(0, _text.Length - 8192); }
        }
        public string Value { get { lock (_text) return _text.ToString(); } }
    }
    private sealed class ListingVerifier
    {
        private readonly Dictionary<string, PackEntry> _expected;
        private readonly HashSet<string> _allowedDirectories = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private string? _path; private long? _size; private bool _directory;
        public ListingVerifier(IReadOnlyList<PackEntry> entries)
        {
            _expected = entries.ToDictionary(entry => FilePackPaths.NormalizeArchivePath(entry.ArchivePath), StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var path = FilePackPaths.NormalizeArchivePath(entry.ArchivePath);
                if (entry.IsDirectory) _allowedDirectories.Add(path);
                foreach (var parent in FilePackPaths.Ancestors(path)) _allowedDirectories.Add(parent);
            }
        }
        public void ReadLine(string line)
        {
            if (line.Length == 0) { FinishEntry(); return; }
            if (line.StartsWith("Path = ", StringComparison.Ordinal)) { FinishEntry(); _path = line[7..].Replace('\\', '/').TrimEnd('/'); }
            else if (line.StartsWith("Size = ", StringComparison.Ordinal) && long.TryParse(line.AsSpan(7), CultureInfo.InvariantCulture, out var size)) _size = size;
            else if (line is "Folder = +" || line.StartsWith("Attributes = D", StringComparison.Ordinal)) _directory = true;
        }
        private void FinishEntry()
        {
            if (_path is null) return;
            var path = FilePackPaths.NormalizeArchivePath(_path);
            if (!_seen.Add(path)) throw new InvalidDataException("压缩包中存在重复路径：" + path);
            if (_expected.TryGetValue(path, out var expected))
            {
                if (_directory != expected.IsDirectory || !_directory && _size != expected.Size)
                    throw new InvalidDataException("压缩包条目的类型或大小不一致：" + path);
            }
            else if (!_directory || !_allowedDirectories.Contains(path)) throw new InvalidDataException("压缩包包含清单外条目：" + path);
            _path = null; _size = null; _directory = false;
        }
        public void Complete()
        {
            FinishEntry();
            foreach (var path in _expected.Keys) if (!_seen.Contains(path)) throw new InvalidDataException("压缩包缺少条目：" + path);
        }
    }
}
