using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DailyToolkit.Core.Conversion;
using DailyToolkit.Desktop.Conversion;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckFormatConversionAsync(string? images = null)
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-conversion-desktop-");
        try
        {
            var source = Path.Combine(fixture.FullName, "透明图片.png");
            var pixels = new byte[32 * 16 * 4];
            for (var y = 0; y < 16; y++) for (var x = 0; x < 32; x++)
            {
                var offset = (y * 32 + x) * 4; pixels[offset + 1] = 160;
                pixels[offset + 3] = x < 16 ? (byte)0 : (byte)255;
            }
            var bitmap = BitmapSource.Create(32, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(source)) encoder.Save(file);
            var original = SHA256.HashData(File.ReadAllBytes(source));
            var backend = new LocalConversionBackend(); var runner = new ConversionRunner(backend);
            var output = Path.Combine(fixture.FullName, "输出");
            var jpg = (await runner.RunAsync(new(source, output, ConversionKind.Image, "jpg", new()))).Single();
            using (var input = File.OpenRead(jpg))
            {
                var image = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                Require(image.PixelWidth == 32 && image.PixelHeight == 16, "Image dimensions changed");
                var rgb = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0); var pixel = new byte[3];
                rgb.CopyPixels(new Int32Rect(4, 4, 1, 1), pixel, 3, 0);
                Require(pixel.All(c => c > 235), "Transparent pixels did not become white in JPG");
            }
            var pdf = (await runner.RunAsync(new(source, output, ConversionKind.Image, "pdf", new()))).Single();
            var pages = await runner.RunAsync(new(pdf, output, ConversionKind.Pdf, "png", new() { PdfDpi = 96 }));
            Require(pages.Count == 1 && File.ReadAllBytes(pages[0]).Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "Image/PDF round-trip did not render a valid PNG page");
            var tiff = Path.Combine(fixture.FullName, "多页.tiff");
            var tiffEncoder = new TiffBitmapEncoder(); tiffEncoder.Frames.Add(BitmapFrame.Create(bitmap)); tiffEncoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(tiff)) tiffEncoder.Save(file);
            var tiffPages = await runner.RunAsync(new(tiff, output, ConversionKind.Image, "png", new()));
            Require(tiffPages.Count == 2 && tiffPages[0].EndsWith("多页-1.png", StringComparison.Ordinal) && tiffPages[1].EndsWith("多页-2.png", StringComparison.Ordinal), "TIFF pages were dropped or reordered");
            var gif = Path.Combine(fixture.FullName, "动画.gif");
            var gifEncoder = new GifBitmapEncoder(); gifEncoder.Frames.Add(BitmapFrame.Create(bitmap)); gifEncoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(gif)) gifEncoder.Save(file);
            await ClipboardThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new(gif, output, ConversionKind.Image, "png", new())));
            Require(original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(source))) && !Directory.EnumerateDirectories(output).Any(), "Original was modified or staging resources remained");
            Console.WriteLine("PASS Local image formats, JPG alpha background, multipage TIFF, PDF creation/rendering, animation rejection and original/staging protection");

            var settings = new ConversionSettingsStore(Path.Combine(fixture.FullName, "conversion.json"));
            using (var model = new ConversionViewModel(settings, backend))
            {
                Require(!model.IsEnabled && model.IsIdleForMemoryCollection && !model.StartCommand.CanExecute(null), "Disabled converter allocated active work");
                model.SetEnabled(true); model.AddFiles([source, source]); model.OutputDirectory = output;
                Require(model.Items.Count == 1 && model.CanStart, "Duplicate filtering or command eligibility failed");
                CheckConversionLayout(model, images);
                model.Kind = ConversionKind.Pdf;
                Require(model.Items.Count == 1, "Changing type silently discarded the file list");
                model.Kind = ConversionKind.Image; model.StartCommand.Execute(null); await model.PendingWork;
                Require(model.Items.Single().State == "完成" && !model.IsBusy, "View model did not finish local conversion");
                model.SetEnabled(false); Require(model.IsIdleForMemoryCollection, "Disabled converter retained active resources");
            }
            var blocking = new BlockingConversionBackend();
            using (var model = new ConversionViewModel(settings, blocking))
            {
                model.SetEnabled(true); model.AddFiles([source]); model.StartCommand.Execute(null);
                await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                model.SetEnabled(false); await model.PendingWork.WaitAsync(TimeSpan.FromSeconds(5));
                Require(blocking.Cancelled && model.IsIdleForMemoryCollection && model.Items.Single().State == "已取消", "Disabling did not cancel and release the operation");
            }
            var blockedSettings = Path.Combine(fixture.FullName, "blocked.json"); Directory.CreateDirectory(blockedSettings);
            using (var model = new ConversionViewModel(new(blockedSettings)))
            { model.SetEnabled(true); Require(!model.IsEnabled && model.HasNotice, "Failed settings save changed enabled state"); }
            Console.WriteLine("PASS Converter deduplication, cancellation/release and failed-save protection");
            await CheckOfficeConversionAsync(fixture.FullName, runner);
            await CheckNativeMediaConversionAsync(fixture.FullName, runner);
        }
        catch (Exception exception) { Console.WriteLine("FAIL Conversion fixture: " + exception); throw; }
        finally { fixture.Delete(true); }
    }

    private static async Task CheckOfficeConversionAsync(string fixture, ConversionRunner runner)
    {
        var engine = System.Environment.GetEnvironmentVariable("DAILYTOOLKIT_TEST_LIBREOFFICE");
        if (string.IsNullOrWhiteSpace(engine) || !File.Exists(engine))
        {
            if (System.Environment.GetEnvironmentVariable("CI") == "true") throw new InvalidOperationException("CI must configure the LibreOffice test engine.");
            Console.WriteLine("SKIP Real LibreOffice conversion: DAILYTOOLKIT_TEST_LIBREOFFICE is not configured."); return;
        }
        var options = new ConversionOptions { LibreOfficePath = engine };
        var existingProfiles = Directory.GetDirectories(Path.GetTempPath(), "dtk-lo-*").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var output = Path.Combine(fixture, "office-output");
        var word = Path.Combine(fixture, "文档 example.docx");
        using (var zip = System.IO.Compression.ZipFile.Open(word, System.IO.Compression.ZipArchiveMode.Create))
        {
            AddZipText(zip, "[Content_Types].xml", """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>
                """);
            AddZipText(zip, "_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
                """);
            AddZipText(zip, "word/document.xml", """
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>格式转换离线检查</w:t></w:r></w:p><w:p><w:r><w:t>Original file stays unchanged.</w:t></w:r></w:p></w:body></w:document>
                """);
        }
        var original = SHA256.HashData(File.ReadAllBytes(word));
        var pdf = (await runner.RunAsync(new(word, output, ConversionKind.Document, "pdf", options))).Single();
        var rendered = await runner.RunAsync(new(pdf, output, ConversionKind.Pdf, "png", new()));
        Require(rendered.Count > 0, "Office PDF was not readable by the Windows PDF renderer");
        var odt = (await runner.RunAsync(new(word, output, ConversionKind.Document, "odt", options))).Single();
        var docx = (await runner.RunAsync(new(odt, output, ConversionKind.Document, "docx", options))).Single();
        var text = (await runner.RunAsync(new(docx, output, ConversionKind.Document, "txt", options))).Single();
        Require(File.ReadAllText(text).Contains("格式转换离线检查", StringComparison.Ordinal), "Word/ODT/TXT conversion lost Unicode text");
        var csv = Path.Combine(fixture, "表格.csv"); File.WriteAllText(csv, "项目,数量\r\n苹果,2\r\n", new System.Text.UTF8Encoding(false));
        var xlsx = (await runner.RunAsync(new(csv, output, ConversionKind.Document, "xlsx", options))).Single();
        var ods = (await runner.RunAsync(new(xlsx, output, ConversionKind.Document, "ods", options))).Single();
        var csvBack = (await runner.RunAsync(new(ods, output, ConversionKind.Document, "csv", options))).Single();
        Require(File.ReadAllText(csvBack).Contains("苹果", StringComparison.Ordinal), "Spreadsheet conversion lost UTF-8 cell text");
        var presentation = Path.Combine(fixture, "演示.odp");
        using (var zip = System.IO.Compression.ZipFile.Open(presentation, System.IO.Compression.ZipArchiveMode.Create))
        {
            AddZipText(zip, "mimetype", "application/vnd.oasis.opendocument.presentation");
            AddZipText(zip, "META-INF/manifest.xml", """
                <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.2"><manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.presentation"/><manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/></manifest:manifest>
                """);
            AddZipText(zip, "content.xml", """
                <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0" office:version="1.2"><office:body><office:presentation><draw:page draw:name="Slide1"><draw:frame svg:x="2cm" svg:y="2cm" svg:width="20cm" svg:height="8cm"><draw:text-box><text:p>离线演示转换</text:p></draw:text-box></draw:frame></draw:page></office:presentation></office:body></office:document-content>
                """);
        }
        var pptx = (await runner.RunAsync(new(presentation, output, ConversionKind.Document, "pptx", options))).Single();
        var slidePdf = (await runner.RunAsync(new(pptx, output, ConversionKind.Document, "pdf", options))).Single();
        Require((await runner.RunAsync(new(slidePdf, output, ConversionKind.Pdf, "png", new()))).Count > 0, "Presentation PDF did not render");
        Require(original.SequenceEqual(SHA256.HashData(File.ReadAllBytes(word))) && !Directory.EnumerateDirectories(output).Any(), "Office changed original or leaked its isolated profile");
        Require(Directory.GetDirectories(Path.GetTempPath(), "dtk-lo-*").All(existingProfiles.Contains), "Office retained a temporary profile after conversion");
        Console.WriteLine("PASS Real local LibreOffice: DOCX/ODT/TXT/PDF, XLSX/ODS/CSV and ODP/PPTX/PDF with Unicode and readable PDF pages");
    }

    private static async Task CheckConversionDocumentMediaAsync()
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-headless-conversion-");
        try
        {
            var runner = new ConversionRunner(new LocalConversionBackend());
            await CheckOfficeConversionAsync(fixture.FullName, runner);
            await CheckNativeMediaConversionAsync(fixture.FullName, runner);
        }
        catch (Exception exception) { Console.WriteLine("FAIL Headless conversion fixture: " + exception); throw; }
        finally { fixture.Delete(true); }
    }

    private static void AddZipText(System.IO.Compression.ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.NoCompression).Open();
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)); writer.Write(text);
    }

    private static async Task CheckNativeMediaConversionAsync(string fixture, ConversionRunner runner)
    {
        var source = Path.Combine(fixture, "native-audio.wav");
        using (var writer = new BinaryWriter(File.Create(source)))
        {
            var pcm = Enumerable.Range(0, 44100).SelectMany(i => BitConverter.GetBytes((short)(Math.Sin(i * .1) * 10000))).ToArray();
            writer.Write("RIFF"u8.ToArray()); writer.Write(36 + pcm.Length); writer.Write("WAVEfmt "u8.ToArray()); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8.ToArray()); writer.Write(pcm.Length); writer.Write(pcm);
        }
        if (System.Environment.GetEnvironmentVariable("CI") == "true")
        {
            var ready = Path.Combine(fixture, "worker-ready");
            using var cancellation = new CancellationTokenSource();
            var worker = ExternalConversionBackend.RunOwnedWorkerAsync(System.Environment.ProcessPath!,
                ["--conversion-worker-hold", source, ready], cancellation.Token);
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(ready))
                {
                    if (worker.IsCompleted) await worker;
                    if (DateTime.UtcNow > deadline) throw new TimeoutException("Conversion worker did not start.");
                    await Task.Delay(25);
                }
                cancellation.Cancel();
                await ClipboardThrowsAsync<OperationCanceledException>(() => worker.WaitAsync(TimeSpan.FromSeconds(5)));
                using var released = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            finally
            {
                cancellation.Cancel();
                try { await worker.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            }
            Console.WriteLine("PASS Owned conversion worker cancellation releases its file handles");
        }
        foreach (var format in new[] { "mp3", "m4a", "wav" })
        {
            using var operation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var result = await runner.RunAsync(new(source, Path.Combine(fixture, "native-media"), ConversionKind.Media, format, new()), cancellationToken: operation.Token);
            Require(result.Count == 1 && new FileInfo(result[0]).Length > 0, "Windows media conversion produced no output");
            using var originalLock = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None);
            using var resultLock = new FileStream(result[0], FileMode.Open, FileAccess.Read, FileShare.None);
        }
        Console.WriteLine("PASS Windows native media: WAV to MP3/M4A/WAV without an external engine");
        var ffmpeg = System.Environment.GetEnvironmentVariable("DAILYTOOLKIT_TEST_FFMPEG");
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
        {
            if (System.Environment.GetEnvironmentVariable("CI") == "true") throw new InvalidOperationException("CI must configure the synthetic video fixture engine.");
            Console.WriteLine("SKIP Windows native video fixture: local FFmpeg generator is not configured."); return;
        }
        var video = Path.Combine(fixture, "native-video.mp4");
        var start = new System.Diagnostics.ProcessStartInfo(ffmpeg)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=440", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", video }) start.ArgumentList.Add(arg);
        using (var process = System.Diagnostics.Process.Start(start)!)
        {
            var errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
            Require(process.ExitCode == 0, "Synthetic native video fixture failed: " + await errors);
        }
        foreach (var format in new[] { "mp4", "wmv" })
        {
            using var operation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var result = await runner.RunAsync(new(video, Path.Combine(fixture, "native-media"), ConversionKind.Media, format, new()), cancellationToken: operation.Token);
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(result.Single());
            using (var stream = await file.OpenReadAsync())
            {
                var media = await Windows.Media.MediaProperties.MediaEncodingProfile.CreateFromStreamAsync(stream);
                Require(media.Video is { Width: 64, Height: 48 } && media.Video.FrameRate.Numerator == 10 * media.Video.FrameRate.Denominator,
                    "Native video conversion changed source dimensions or frame rate");
            }
            using var originalLock = new FileStream(video, FileMode.Open, FileAccess.Read, FileShare.None);
            using var resultLock = new FileStream(result[0], FileMode.Open, FileAccess.Read, FileShare.None);
        }
        Console.WriteLine("PASS Windows native video: MP4/WMV preserve dimensions/frame rate and release file handles");
    }

    private static void CheckConversionLayout(ConversionViewModel model, string? images)
    {
        var view = new ConversionView { DataContext = model, FontFamily = new FontFamily("Microsoft YaHei UI") };
        foreach (var dark in new[] { false, true })
        {
            var palette = dark ? ThemePalette.Dark : new ThemePalette();
            foreach (var field in typeof(ThemePalette).GetProperties().Where(p => p.GetMethod is { IsStatic: false }))
                view.Resources[field.Name + "Brush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString((string)field.GetValue(palette)!));
            view.Background = (Brush)view.Resources["WindowBrush"];
            foreach (var width in new[] { 420, 800 })
            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
            {
                view.Width = width; view.Height = 920; view.Measure(new Size(width, 920)); view.Arrange(new Rect(0, 0, width, 920)); view.UpdateLayout();
                Require(view.ActualWidth <= width && ((DataGrid)view.FindName("FileList")).EnableRowVirtualization, "Converter layout overflowed or lost virtualization");
                if (images is null) continue;
                Directory.CreateDirectory(images);
                var rendered = new RenderTargetBitmap((int)(width * scale), (int)(920 * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                rendered.Render(view);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
                using var file = File.Create(Path.Combine(images, $"conversion-{(dark ? "dark" : "light")}-{width}-{scale * 100:0}.png")); encoder.Save(file);
            }
        }
        view.DataContext = null;
    }

    private sealed class BlockingConversionBackend : IConversionBackend
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Cancelled { get; private set; }
        public async Task<IReadOnlyList<ConvertedFile>> ConvertAsync(ConversionRequest request, string workspace, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return [];
        }
    }
}
