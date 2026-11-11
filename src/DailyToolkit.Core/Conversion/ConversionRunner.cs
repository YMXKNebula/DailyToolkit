namespace DailyToolkit.Core.Conversion;

public sealed record ConversionRequest(string Source, string OutputDirectory, ConversionKind Kind,
    string Target, ConversionOptions Options);
public sealed record ConvertedFile(string Path, string Name);

public interface IConversionBackend
{
    Task<IReadOnlyList<ConvertedFile>> ConvertAsync(ConversionRequest request, string workspace,
        IProgress<double>? progress, CancellationToken cancellationToken);
}

// Each input is staged beside its destination. Only complete files are committed,
// without replacement; cancellation or a failed commit rolls back this input.
public sealed class ConversionRunner(IConversionBackend backend)
{
    private static readonly object PublishGate = new();
    public async Task<IReadOnlyList<string>> RunAsync(ConversionRequest request,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ConversionFormats.Validate(request.Kind, request.Source, request.Target);
        var source = LocalPath(request.Source);
        var destination = LocalPath(request.OutputDirectory);
        if (!File.Exists(source)) throw new FileNotFoundException("原文件不存在。", source);
        using var sourceLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        var workspace = Path.Combine(destination, ".dailytoolkit-convert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        var committed = new List<string>();
        try
        {
            var outputs = await backend.ConvertAsync(request with { Source = source,
                OutputDirectory = destination, Options = request.Options.Normalize() }, workspace, progress, cancellationToken).ConfigureAwait(false);
            if (outputs.Count is 0 or > 1000) throw new IOException("转换未生成有效文件，或页数超过限制。");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var output in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.GetFullPath(output.Path);
                if (!string.Equals(Path.GetDirectoryName(path), workspace, StringComparison.Ordinal) ||
                    Path.GetFileName(output.Name) != output.Name || string.IsNullOrWhiteSpace(output.Name) ||
                    output.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    !seen.Add(path) || !File.Exists(path) || new FileInfo(path).Length == 0 ||
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    !Path.GetExtension(output.Name).Equals("." + request.Target, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("转换结果校验失败，未替换原文件。", new IOException(
                        $"Output={path}; workspace={workspace}; name={output.Name}; exists={File.Exists(path)}; " +
                        $"length={(File.Exists(path) ? new FileInfo(path).Length : -1)}; attributes={(File.Exists(path) ? File.GetAttributes(path) : 0)}"));
            }
            foreach (var output in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stem = Path.GetFileNameWithoutExtension(output.Name);
                var extension = Path.GetExtension(output.Name);
                var moved = false;
                for (var suffix = 0; suffix < 10000; suffix++)
                {
                    var path = Path.Combine(destination, stem + (suffix == 0 ? "" : $" ({suffix + 1})") + extension);
                    try
                    {
                        // Serialize this app's name selection and commit. The
                        // explicit existence check also protects Unix filesystems
                        // whose rename fallback cannot atomically reject a rival.
                        lock (PublishGate)
                        {
                            if (File.Exists(path) || Directory.Exists(path)) continue;
                            File.Move(output.Path, path, overwrite: false);
                            committed.Add(path); moved = true;
                        }
                        break;
                    }
                    catch (IOException) when (File.Exists(path) || Directory.Exists(path)) { }
                }
                if (!moved) throw new IOException("同名文件过多，请更换输出位置。");
            }
            return committed;
        }
        catch
        {
            foreach (var path in committed) TryDeleteFile(path);
            throw;
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.WriteLine(e); }
        }
    }

    public static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("请选择本机上的完整文件路径。");
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network)
            throw new ArgumentException("请选择本机磁盘上的文件和输出位置。");
        return full;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine(e); }
    }
}
