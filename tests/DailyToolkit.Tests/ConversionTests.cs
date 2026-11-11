using System.Security.Cryptography;
using System.Text;
using DailyToolkit.Core.Conversion;

namespace DailyToolkit.Tests;

internal static partial class Program
{
    private static void RegisterConversionTests()
    {
        Test("Conversion validates source families and rejects remote paths", () =>
        {
            ConversionFormats.Validate(ConversionKind.Document, "演示.pptx", "pdf");
            ConversionFormats.Validate(ConversionKind.Media, "电影.mp4", "mp3");
            foreach (var (kind, source, target) in new[] {
                (ConversionKind.Document, "表格.xlsx", "docx"), (ConversionKind.Document, "文章.docx", "xlsx"),
                (ConversionKind.Media, "声音.wav", "mp4"), (ConversionKind.Pdf, "文章.pdf", "docx"),
                (ConversionKind.Image, "照片.png", "exe") })
            {
                var rejected = false;
                try { ConversionFormats.Validate(kind, source, target); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "Incompatible conversion was accepted");
            }
            foreach (var remote in new[] { "https://example.invalid/file.png", "\\\\server\\share\\file.png", "//server/share/file.png", "relative.png" })
            {
                var rejected = false;
                try { ConversionRunner.LocalPath(remote); } catch (ArgumentException) { rejected = true; }
                Require(rejected, "Remote or relative path was accepted");
            }
        });
        Test("Animated PNG is rejected without changing decoder stream position", () =>
        {
            using var animated = new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAACGFjVEwAAAACAAAAAPONk3AAAAAaZmNUTAAAAAAAAAABAAAAAQAAAAAAAAAAAAEACgAAWn8w0AAAAA1JREFUeJxj+M/A8B8ABQAB/4mZPR0AAAAaZmNUTAAAAAEAAAABAAAAAQAAAAAAAAAAAAEACgAAwQzaBAAAABFmZEFUAAAAAnicY2D4z/AfAAQBAf9i5+mcAAAAAElFTkSuQmCC"));
            var rejected = false;
            try { ImageInputPolicy.RejectAnimatedPng(animated); } catch (InvalidOperationException) { rejected = true; }
            Require(rejected && animated.Position == 0, "APNG lost frames or stream position");
            using var other = new MemoryStream("not a PNG"u8.ToArray());
            ImageInputPolicy.RejectAnimatedPng(other);
            Require(other.Position == 0, "Other image stream position changed");
        });
        AsyncTest("Conversion preserves originals and atomically avoids existing output names", async () =>
        {
            var fixture = Directory.CreateTempSubdirectory("DailyToolkit-conversion-");
            try
            {
                var source = Path.Combine(fixture.FullName, "照片.png");
                await File.WriteAllTextAsync(source, "original bytes");
                var before = SHA256.HashData(await File.ReadAllBytesAsync(source));
                var runner = new ConversionRunner(new FixtureConversionBackend(async (_, workspace, token) =>
                {
                    var path = Path.Combine(workspace, "generated.png");
                    await File.WriteAllTextAsync(path, "converted bytes", token);
                    return [new(path, "照片.png")];
                }));
                var request = new ConversionRequest(source, fixture.FullName, ConversionKind.Image, "png", new());
                var outputs = await Task.WhenAll(runner.RunAsync(request), runner.RunAsync(request));
                Require(outputs.SelectMany(x => x).Distinct().Count() == 2, "Concurrent conversions overwrote one another: " + string.Join(" / ", outputs.SelectMany(x => x)));
                var after = SHA256.HashData(await File.ReadAllBytesAsync(source));
                Require(before.SequenceEqual(after), "Original was changed");
                Require(outputs.SelectMany(x => x).All(p => File.ReadAllText(p) == "converted bytes"), "Committed output is incomplete");
                Require(!Directory.EnumerateDirectories(fixture.FullName).Any(), "Staging directories leaked");
            }
            finally { fixture.Delete(true); }
        });
        AsyncTest("Conversion failure and cancellation clean only their own incomplete outputs", async () =>
        {
            var fixture = Directory.CreateTempSubdirectory("DailyToolkit-conversion-cancel-");
            try
            {
                var source = Path.Combine(fixture.FullName, "source.png"); await File.WriteAllTextAsync(source, "source");
                var request = new ConversionRequest(source, fixture.FullName, ConversionKind.Image, "png", new());
                var runner = new ConversionRunner(new FixtureConversionBackend(async (_, workspace, token) =>
                {
                    await File.WriteAllTextAsync(Path.Combine(workspace, "partial.png"), "partial", token);
                    throw new IOException("fixture failure");
                }));
                await Expect<IOException>(() => runner.RunAsync(request));
                using var cancellation = new CancellationTokenSource();
                runner = new(new FixtureConversionBackend(async (_, workspace, token) =>
                {
                    var path = Path.Combine(workspace, "partial.png"); await File.WriteAllTextAsync(path, "partial", token);
                    cancellation.Cancel(); return [new(path, "result.png")];
                }));
                await Expect<OperationCanceledException>(() => runner.RunAsync(request, cancellationToken: cancellation.Token));
                Require(File.ReadAllText(source) == "source" && Directory.GetFileSystemEntries(fixture.FullName).Length == 1,
                    "Failure/cancel damaged source or left an incomplete result");
            }
            finally { fixture.Delete(true); }
        });
        AsyncTest("Conversion validates every page before publishing and rejects traversal or empty output", async () =>
        {
            var fixture = Directory.CreateTempSubdirectory("DailyToolkit-conversion-validation-");
            try
            {
                var source = Path.Combine(fixture.FullName, "source.pdf"); await File.WriteAllTextAsync(source, "fixture");
                foreach (var invalid in new[] { "../escape.png", "wrong.jpg", "empty.png" })
                {
                    var runner = new ConversionRunner(new FixtureConversionBackend(async (_, workspace, token) =>
                    {
                        var first = Path.Combine(workspace, "one.png"); await File.WriteAllTextAsync(first, "one", token);
                        var last = Path.Combine(workspace, "two.png"); await File.WriteAllTextAsync(last, invalid == "empty.png" ? "" : "two", token);
                        return [new(first, "valid.png"), new(last, invalid)];
                    }));
                    await Expect<IOException>(() => runner.RunAsync(new(source, fixture.FullName, ConversionKind.Pdf, "png", new())));
                    Require(Directory.GetFileSystemEntries(fixture.FullName).Length == 1, "A partial page set was published");
                }
            }
            finally { fixture.Delete(true); }
        });
        Test("Image PDF has correct page tree, byte offsets and culture-independent dimensions", () =>
        {
            var oldCulture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
                using var stream = new MemoryStream();
                var writer = new ImagePdfWriter(stream);
                writer.AddPage([0xff, 0xd8, 0xff, 0xd9], 100, 200, 96, 96);
                writer.AddPage([0xff, 0xd8, 0xff, 0xd9], 200, 100, 96, 96); writer.Complete();
                var bytes = stream.ToArray(); var text = Encoding.Latin1.GetString(bytes);
                Require(text.Contains("/Count 2") && text.Contains("/MediaBox [0 0 75 150]"), "PDF page size/tree is invalid");
                var lines = text[text.LastIndexOf("xref\n0 ", StringComparison.Ordinal)..].Split('\n');
                for (var i = 1; i <= 8; i++)
                {
                    var offset = int.Parse(lines[i + 2][..10], System.Globalization.CultureInfo.InvariantCulture);
                    Require(Encoding.ASCII.GetString(bytes, offset, $"{i} 0 obj".Length) == $"{i} 0 obj", "Invalid PDF xref byte offset");
                }
                using var lossless = new MemoryStream();
                var rgbWriter = new ImagePdfWriter(lossless);
                byte[] rgb = [255, 0, 0, 0, 255, 0];
                rgbWriter.AddRgbPage(2, 1, output => output.Write(rgb)); rgbWriter.Complete();
                var rgbPdf = lossless.ToArray(); var rgbText = Encoding.Latin1.GetString(rgbPdf);
                Require(rgbText.Contains("/FlateDecode") && !rgbText.Contains("/DCTDecode"), "Image PDF added lossy JPEG encoding");
                var payload = rgbText.IndexOf("stream\n", StringComparison.Ordinal) + 7;
                using var compressed = new MemoryStream(rgbPdf, payload, rgbPdf.Length - payload);
                using var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionMode.Decompress);
                using var restored = new MemoryStream(); zlib.CopyTo(restored);
                Require(restored.ToArray().SequenceEqual(rgb), "PDF lossless RGB payload changed pixels");
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = oldCulture; }
        });
        Test("Office conversion rejects external relationships and embedded macros", () =>
        {
            var fixture = Directory.CreateTempSubdirectory("DailyToolkit-office-validation-");
            try
            {
                foreach (var name in new[] { "word/_rels/document.xml.rels", "word/vbaProject.bin" })
                {
                    var path = Path.Combine(fixture.FullName, "document.docx");
                    using (var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
                    using (var stream = archive.CreateEntry(name).Open())
                    using (var writer = new StreamWriter(stream))
                        writer.Write("<Relationships><Relationship TargetMode=\"External\" Target=\"https://example.invalid/\"/></Relationships>");
                    var rejected = false;
                    try { ExternalConversionBackend.ValidateDocument(path); } catch (InvalidOperationException) { rejected = true; }
                    Require(rejected, "External relationship or macro was accepted"); File.Delete(path);
                }
            }
            finally { fixture.Delete(true); }
        });
        AsyncTest("Local FFmpeg round-trip preserves lossless audio and handles Unicode paths without a shell", CheckConversionFFmpegAsync);
    }

    private static async Task CheckConversionFFmpegAsync()
    {
        var engine = System.Environment.GetEnvironmentVariable("DAILYTOOLKIT_TEST_FFMPEG");
        if (string.IsNullOrWhiteSpace(engine) || !File.Exists(engine)) { Console.WriteLine("SKIP Real FFmpeg conversion: DAILYTOOLKIT_TEST_FFMPEG is not configured."); return; }
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-音频-转换-");
        try
        {
            var source = Path.Combine(fixture.FullName, "声音 [一]; sample.wav");
            var pcm = Enumerable.Range(0, 8000).SelectMany(i => BitConverter.GetBytes((short)(Math.Sin(i * .1) * 10000))).ToArray();
            using (var writer = new BinaryWriter(File.Create(source)))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + pcm.Length); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(pcm.Length); writer.Write(pcm);
            }
            var original = await File.ReadAllBytesAsync(source);
            var output = Path.Combine(fixture.FullName, "输出");
            var runner = new ConversionRunner(new ExternalConversionBackend());
            var options = new ConversionOptions { FFmpegPath = engine };
            var flac = (await runner.RunAsync(new(source, output, ConversionKind.Media, "flac", options))).Single();
            var wav = (await runner.RunAsync(new(flac, output, ConversionKind.Media, "wav", options))).Single();
            using var read = new BinaryReader(File.OpenRead(wav)); read.BaseStream.Position = 12;
            byte[]? decoded = null;
            while (read.BaseStream.Position + 8 <= read.BaseStream.Length)
            {
                var chunk = Encoding.ASCII.GetString(read.ReadBytes(4)); var size = read.ReadUInt32();
                if (chunk == "data") { decoded = read.ReadBytes((int)size); break; }
                read.BaseStream.Position += size + (size & 1);
            }
            Require(decoded is not null && decoded.SequenceEqual(pcm), "Lossless audio samples changed after round-trip");
            foreach (var format in new[] { "mp3", "m4a", "ogg" })
            {
                var result = await runner.RunAsync(new(source, output, ConversionKind.Media, format, options));
                Require(result.Count == 1 && new FileInfo(result[0]).Length > 0, "Audio encode/decode validation failed: " + format);
            }
            var video = Path.Combine(fixture.FullName, "视频 sample.mkv");
            var start = new System.Diagnostics.ProcessStartInfo(engine) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=64x48:r=12:d=1",
                "-i", source, "-shortest", "-c:v", "ffv1", "-c:a", "pcm_s16le", video }) start.ArgumentList.Add(argument);
            using (var process = System.Diagnostics.Process.Start(start)!)
            {
                var error = process.StandardError.ReadToEndAsync(); var stdout = process.StandardOutput.ReadToEndAsync();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
                await stdout; Require(process.ExitCode == 0, "Video fixture generation failed: " + await error);
            }
            foreach (var format in new[] { "mp4", "wmv", "mkv", "webm", "mp3" })
            {
                var result = await runner.RunAsync(new(video, output, ConversionKind.Media, format, options));
                Require(result.Count == 1 && new FileInfo(result[0]).Length > 0, "Video encode/decode validation failed: " + format);
            }
            var originalAfter = await File.ReadAllBytesAsync(source);
            Require(original.SequenceEqual(originalAfter), "Original audio was modified");
            Require(!Directory.EnumerateDirectories(output).Any(), "FFmpeg workspace was retained");
        }
        finally { fixture.Delete(true); }
    }

    private sealed class FixtureConversionBackend(Func<ConversionRequest, string, CancellationToken, Task<IReadOnlyList<ConvertedFile>>> convert) : IConversionBackend
    {
        public Task<IReadOnlyList<ConvertedFile>> ConvertAsync(ConversionRequest request, string workspace, IProgress<double>? progress, CancellationToken cancellationToken)
            => convert(request, workspace, cancellationToken);
    }
}
