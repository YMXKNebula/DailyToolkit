using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using DailyToolkit.Core.FilePack;

namespace DailyToolkit.Core.Conversion;

public sealed class ExternalConversionBackend : IConversionBackend
{
    public static Task RunOwnedWorkerAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        RunProcessAsync(executable, arguments, cancellationToken);

    public async Task<IReadOnlyList<ConvertedFile>> ConvertAsync(ConversionRequest request, string workspace,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ConversionFormats.Validate(request.Kind, request.Source, request.Target);
        var output = Path.Combine(workspace, "converted." + request.Target);
        if (request.Kind == ConversionKind.Document)
        {
            ValidateDocument(request.Source, cancellationToken);
            // LibreOffice creates deeply nested extension-cache paths. Keep its
            // private profile short even when the output path is already long.
            var profile = Directory.CreateTempSubdirectory("dtk-lo-").FullName;
            try
            {
                Directory.CreateDirectory(Path.Combine(profile, "user"));
                File.WriteAllText(Path.Combine(profile, "user", "registrymodifications.xcu"), OfficeProfile);
                var inputDirectory = Path.Combine(workspace, "input");
                Directory.CreateDirectory(inputDirectory);
                var input = Path.Combine(inputDirectory, Path.GetFileName(request.Source));
                await using (var source = new FileStream(request.Source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true))
                await using (var copy = new FileStream(input, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await source.CopyToAsync(copy, cancellationToken).ConfigureAwait(false);
                var filter = request.Target switch
                {
                    "csv" => "csv:Text - txt - csv (StarCalc):44,34,76,1",
                    "txt" => "txt:Text (encoded):UTF8",
                    _ => request.Target
                };
                var office = request.Options.LibreOfficePath;
                if (OperatingSystem.IsWindows() && Path.GetFileName(office).Equals("soffice.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var console = Path.Combine(Path.GetDirectoryName(office) ?? "", "soffice.com");
                    if (File.Exists(console)) office = console;
                }
                var engineLog = await RunProcessAsync(office,
                    ["-env:UserInstallation=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri,
                        "--headless", "--nologo", "--nodefault", "--nofirststartwizard", "--norestore",
                        "--convert-to", filter, "--outdir", workspace, input], cancellationToken).ConfigureAwait(false);
                output = Path.Combine(workspace, Path.GetFileNameWithoutExtension(request.Source) + "." + request.Target);
                if (!File.Exists(output))
                    throw new IOException("文档转换未生成结果，请检查文件格式或保护状态。",
                        new IOException(engineLog + "\nInput=" + input + "; profile=" + new Uri(profile + Path.DirectorySeparatorChar).AbsoluteUri +
                            "\nGenerated files: " + string.Join(", ", Directory.EnumerateFiles(workspace).Select(Path.GetFileName))));
                if (File.Exists(output) && request.Target is not ("pdf" or "csv" or "txt")) ValidateDocument(output, cancellationToken);
                if (File.Exists(output) && request.Target == "pdf")
                {
                    using var document = File.OpenRead(output);
                    var signature = new byte[5];
                    if (document.Read(signature) != 5 || !signature.SequenceEqual("%PDF-"u8.ToArray())) throw new IOException("文档引擎未生成有效 PDF。");
                }
            }
            finally
            {
                try { Directory.Delete(profile, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Trace.WriteLine(e); }
            }
        }
        else
        {
            if (request.Kind == ConversionKind.Image && request.Target == "pdf")
                throw new InvalidOperationException("WebP 请先转换为 PNG 或 JPG，再生成 PDF。");
            await RunProcessAsync(request.Options.FFmpegPath, FFmpegArguments(request, output), cancellationToken).ConfigureAwait(false);
            // Decode the completed result before publishing it. This also catches
            // unsupported encoders or a damaged/truncated container from a backend.
            await RunProcessAsync(request.Options.FFmpegPath,
                ["-nostdin", "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file,pipe", "-i", output,
                    "-threads", "2", "-map", "0:v:0?", "-map", "0:a:0?", "-f", "null", "-"], cancellationToken).ConfigureAwait(false);
        }
        progress?.Report(100);
        return [new(output, Path.GetFileNameWithoutExtension(request.Source) + "." + request.Target)];
    }

    public static IReadOnlyList<string> FFmpegArguments(ConversionRequest request, string output)
    {
        var args = new List<string> { "-nostdin", "-hide_banner", "-loglevel", "error", "-n", "-filter_threads", "2",
            "-protocol_whitelist", "file,pipe", "-i", request.Source, "-threads", "2", "-map_metadata", "-1" };
        if (request.Kind == ConversionKind.Image)
        {
            // Animated WebP is not flattened silently; the desktop backend checks
            // its RIFF animation flag before dispatching this single-image path.
            args.AddRange(["-frames:v", "1"]);
            if (request.Target == "webp") args.AddRange(["-c:v", "libwebp", "-quality", request.Options.Quality.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            else if (request.Target == "jpg") args.AddRange(["-q:v", "2"]);
        }
        else if (!ConversionFormats.IsVideoTarget(request.Target))
        {
            args.AddRange(["-map", "0:a:0", "-vn"]);
            args.AddRange(request.Target switch
            {
                "mp3" => ["-c:a", "libmp3lame", "-b:a", "192k"],
                "m4a" => ["-c:a", "aac", "-b:a", "192k"],
                "wav" => ["-c:a", "pcm_s16le"],
                "flac" => ["-c:a", "flac"],
                "ogg" => ["-c:a", "libvorbis", "-q:a", "5"],
                _ => throw new InvalidOperationException("不支持此音频格式。")
            });
        }
        else
        {
            args.AddRange(["-map", "0:v:0", "-map", "0:a:0?", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-pix_fmt", "yuv420p"]);
            args.AddRange(request.Target switch
            {
                "webm" => ["-c:v", "libvpx-vp9", "-crf", "30", "-b:v", "0", "-c:a", "libopus"],
                "wmv" => ["-c:v", "wmv2", "-b:v", "4M", "-c:a", "wmav2"],
                _ => ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-c:a", "aac", "-b:a", "192k"]
            });
            if (request.Target == "mp4") args.AddRange(["-movflags", "+faststart"]);
        }
        args.Add(output);
        return args;
    }

    private static async Task<string> RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("请先在本地引擎中选择对应的程序。");
        executable = ConversionRunner.LocalPath(executable);
        if (!File.Exists(executable)) throw new FileNotFoundException("本地引擎程序不存在。", executable);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        if (Path.GetFileName(executable).Equals("soffice.com", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(executable).Equals("soffice.exe", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var name in new[] { "URE_BOOTSTRAP", "UNO_PATH", "UNO_SERVICES", "UNO_TYPES", "PYTHONHOME", "PYTHONPATH" })
                start.Environment.Remove(name);
        }
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        using var job = CreateProcessJob();
        Task<string>? stdout = null, stderr = null;
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (!process.Start()) throw new IOException("本地引擎未能启动。");
            try { job?.Assign(process); }
            catch (IOException e) { throw new IOException("无法保护本次转换，已停止任务。", e); }
            process.StandardInput.Close();
            stdout = DrainAsync(process.StandardOutput);
            stderr = DrainAsync(process.StandardError);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var detail = (await stderr).Trim();
                throw new IOException("本地引擎转换失败：" + detail[..Math.Min(detail.Length, 1200)],
                    new IOException($"Engine={Path.GetFileName(executable)}; exit={process.ExitCode}; output={(await stdout).Trim()}"));
            }
            return (await stdout).Trim() + "\n" + (await stderr).Trim();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("转换超过两小时，已停止本次任务。"); }
        finally
        {
            // Only this operation's process tree and private Windows job are owned.
            // LibreOffice always receives a unique profile, never the user's session.
            try
            {
                if (process.Id != 0 && !process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { Trace.WriteLine(e); }
            job?.Dispose();
            try { if (process.Id != 0) await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { Trace.WriteLine(e); }
            if (stdout is not null && stderr is not null)
            {
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (Exception e) when (e is IOException or TimeoutException or ObjectDisposedException) { Trace.WriteLine(e); }
            }
        }
    }

    private static FilePackProcessJob? CreateProcessJob()
    {
        try { return FilePackProcessJob.Create(); }
        catch (IOException e) { throw new IOException("无法建立本次转换的进程保护。", e); }
    }

    private static Task<string> DrainAsync(StreamReader reader) => Task.Factory.StartNew(() =>
    {
        // Process redirects have synchronous pipe handles. Dedicated, operation-
        // scoped readers keep those blocking reads out of the shared thread pool.
        var tail = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > 16384) tail.Remove(0, tail.Length - 16384);
        }
        return tail.ToString();
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public static void ValidateDocument(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension == ".txt") return;
        if (extension == ".csv")
        {
            using var text = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[4096]; var previous = ""; int count;
            while ((count = text.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var section = previous + new string(buffer, 0, count);
                if (HasExternalFormula(section)) throw new InvalidOperationException("表格含外部数据公式，请先保存为静态副本。");
                previous = section[^Math.Min(256, section.Length)..];
            }
            return;
        }
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 10000) throw new IOException("文档内容过多。");
        long xmlBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("Scripts/", StringComparison.OrdinalIgnoreCase) ||
                entry.FullName.StartsWith("Basic/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请使用不含宏和脚本的文档副本。");
            if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
            xmlBytes += entry.Length;
            if (entry.Length > 16 * 1024 * 1024 || xmlBytes > 64 * 1024 * 1024) throw new IOException("文档结构过大。");
            using var stream = entry.Open();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
            var xml = XDocument.Load(reader);
            if (xml.Descendants().Any(e =>
                e.Name.LocalName == "Relationship" && (string?)e.Attribute("TargetMode") == "External" &&
                    !((string?)e.Attribute("Type") ?? "").EndsWith("/hyperlink", StringComparison.Ordinal) ||
                e.Name.LocalName != "a" && e.Attributes().Any(a => a.Name.LocalName == "href" &&
                    (a.Value.Contains(':') || a.Value.StartsWith('/') || a.Value.StartsWith("..", StringComparison.Ordinal))) ||
                (e.Name.LocalName == "instrText" || e.Attributes().Any(a => a.Name.LocalName == "instr")) &&
                    Regex.IsMatch(e.Value + string.Join(' ', e.Attributes().Select(a => a.Value)), @"\b(INCLUDEPICTURE|INCLUDETEXT|DDEAUTO|DDE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
                throw new InvalidOperationException("文档含外部引用，请先将引用内容嵌入本机副本。");
            if (xml.Descendants().Any(e => e.Name.LocalName is "f" or "formula" && HasExternalFormula(e.Value)) ||
                xml.Descendants().Attributes().Any(a => a.Name.LocalName == "formula" && HasExternalFormula(a.Value)))
                throw new InvalidOperationException("文档含外部数据公式，请先保存为静态副本。");
        }
    }

    private static bool HasExternalFormula(string value) => Regex.IsMatch(value, @"\b(WEBSERVICE|DDE)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private const string OfficeProfile = """
        <?xml version="1.0" encoding="UTF-8"?>
        <oor:items xmlns:oor="http://openoffice.org/2001/registry">
        <item oor:path="/org.openoffice.Office.Common/Security/Scripting"><prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop></item>
        <item oor:path="/org.openoffice.Office.Jobs/Jobs/UpdateCheck/Arguments"><prop oor:name="AutoCheckEnabled" oor:op="fuse"><value>false</value></prop></item>
        <item oor:path="/org.openoffice.Office.Common/Misc"><prop oor:name="FirstRun" oor:op="fuse"><value>false</value></prop></item>
        <item oor:path="/org.openoffice.Office.Calc/Content/Update"><prop oor:name="Link" oor:op="fuse"><value>0</value></prop></item>
        </oor:items>
        """;
}
