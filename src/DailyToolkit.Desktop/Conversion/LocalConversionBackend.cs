using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DailyToolkit.Core.Conversion;
using Windows.Data.Pdf;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DailyToolkit.Desktop.Conversion;

public sealed class LocalConversionBackend : IConversionBackend
{
    private const long MaximumPixels = 40_000_000;
    private readonly ExternalConversionBackend _external = new();
    private readonly string? _bundleDirectory;
    private readonly bool _useBundledEngines;

    public LocalConversionBackend() : this(null, true) { }
    internal LocalConversionBackend(string? bundleDirectory, bool useBundledEngines)
    { _bundleDirectory = bundleDirectory; _useBundledEngines = useBundledEngines; }

    public Task<IReadOnlyList<ConvertedFile>> ConvertAsync(ConversionRequest request, string workspace,
        IProgress<double>? progress, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        if (_useBundledEngines)
            request = request with { Options = BundledConversionEngines.Resolve(request.Options, _bundleDirectory) };
        if (request.Kind == ConversionKind.Document ||
            request.Kind == ConversionKind.Media && !string.IsNullOrWhiteSpace(request.Options.FFmpegPath))
        {
            BundledConversionEngines.Require(request.Kind == ConversionKind.Document ? request.Options.LibreOfficePath : request.Options.FFmpegPath);
            return await _external.ConvertAsync(request, workspace, progress, cancellationToken).ConfigureAwait(false);
        }
        if (request.Kind == ConversionKind.Image)
        {
            if (request.Target == "webp" || Path.GetExtension(request.Source).Equals(".webp", StringComparison.OrdinalIgnoreCase))
                BundledConversionEngines.Require(request.Options.FFmpegPath);
            var originalStem = Path.GetFileNameWithoutExtension(request.Source);
            if (Path.GetExtension(request.Source).Equals(".webp", StringComparison.OrdinalIgnoreCase))
            {
                ValidateStaticWebP(request.Source);
                var decoded = await _external.ConvertAsync(request with { Target = "png" }, workspace, null, cancellationToken).ConfigureAwait(false);
                request = request with { Source = decoded[0].Path };
            }
            if (request.Target == "webp")
            {
                // Decode all source pages before WebP encoding, so animated GIF
                // is rejected and multipage TIFF is never reduced to one frame.
                var pages = ConvertImages(request with { Target = "png" }, workspace, null, cancellationToken);
                var result = new List<ConvertedFile>();
                for (var i = 0; i < pages.Count; i++)
                {
                    var engineWorkspace = Path.Combine(workspace, "webp-" + i);
                    Directory.CreateDirectory(engineWorkspace);
                    var encoded = await _external.ConvertAsync(request with { Source = pages[i].Path }, engineWorkspace, null, cancellationToken).ConfigureAwait(false);
                    var path = Path.Combine(workspace, $"page-{i + 1}.webp");
                    File.Move(encoded[0].Path, path);
                    result.Add(new(path, originalStem + (pages.Count == 1 ? "" : $"-{i + 1}") + ".webp"));
                    progress?.Report((i + 1) * 100.0 / pages.Count);
                }
                return result;
            }
            var converted = ConvertImages(request, workspace, progress, cancellationToken);
            var temporaryStem = Path.GetFileNameWithoutExtension(request.Source);
            return converted.Select(f => f with { Name = originalStem + f.Name[temporaryStem.Length..] }).ToArray();
        }
        return request.Kind switch
        {
            ConversionKind.Pdf => await ConvertPdfAsync(request, workspace, progress, cancellationToken).ConfigureAwait(false),
            ConversionKind.Media => await NativeMediaWorker.ConvertAsync(request, workspace, progress, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException("不支持此转换类型。")
        };
    }, cancellationToken);

    private static IReadOnlyList<ConvertedFile> ConvertImages(ConversionRequest request, string workspace,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (new FileInfo(request.Source).Length > 512L * 1024 * 1024) throw new IOException("图片文件超过 512 MB。");
        using var input = new FileStream(request.Source, FileMode.Open, FileAccess.Read, FileShare.Read);
        ImageInputPolicy.RejectAnimatedPng(input, cancellationToken);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);
        if (decoder.Frames.Count is 0 or > 500) throw new IOException("图片页数超过限制。");
        if (decoder is GifBitmapDecoder && decoder.Frames.Count > 1)
            throw new InvalidOperationException("暂不转换动态图，请选择静态图片。");
        var outputs = new List<ConvertedFile>();
        var stem = Path.GetFileNameWithoutExtension(request.Source);
        var combined = request.Target is "pdf" or "tiff";
        using var pdfStream = request.Target == "pdf" ? new FileStream(Path.Combine(workspace, "converted.pdf"), FileMode.CreateNew, FileAccess.Write) : null;
        var pdf = pdfStream is null ? null : new ImagePdfWriter(pdfStream);
        var tiff = request.Target == "tiff" ? new TiffBitmapEncoder { Compression = TiffCompressOption.Zip } : null;
        long totalPixels = 0;
        for (var i = 0; i < decoder.Frames.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = Orient(decoder.Frames[i]);
            CheckSize(image.PixelWidth, image.PixelHeight);
            totalPixels += (long)image.PixelWidth * image.PixelHeight;
            if (tiff is not null && totalPixels > 80_000_000) throw new IOException("多页 TIFF 总像素过大，请分批转换。");
            if (pdf is not null)
            {
                pdf.AddRgbPage(image.PixelWidth, image.PixelHeight, stream => WriteRgbRows(image, stream, cancellationToken), image.DpiX, image.DpiY);
            }
            else if (tiff is not null) tiff.Frames.Add(BitmapFrame.Create(image));
            else
            {
                var name = stem + (decoder.Frames.Count == 1 ? "" : $"-{i + 1}") + "." + request.Target;
                var path = Path.Combine(workspace, $"page-{i + 1}." + request.Target);
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                    SaveImage(image, request.Target, request.Options.Quality, output, cancellationToken);
                outputs.Add(new(path, name));
            }
            progress?.Report((i + 1) * 100.0 / decoder.Frames.Count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (pdf is not null) pdf.Complete();
        if (tiff is not null)
        {
            using var output = new FileStream(Path.Combine(workspace, "converted.tiff"), FileMode.CreateNew, FileAccess.Write);
            tiff.Save(output);
        }
        if (combined) outputs.Add(new(Path.Combine(workspace, "converted." + request.Target), stem + "." + request.Target));
        return outputs;
    }

    private static async Task<IReadOnlyList<ConvertedFile>> ConvertPdfAsync(ConversionRequest request, string workspace,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(request.Source).AsTask(cancellationToken).ConfigureAwait(false);
        using var source = await file.OpenReadAsync().AsTask(cancellationToken).ConfigureAwait(false);
        var pdf = await PdfDocument.LoadFromStreamAsync(source).AsTask(cancellationToken).ConfigureAwait(false);
        if (pdf.PageCount is 0 or > 500) throw new IOException("PDF 页数必须在 1–500 页之间。");
        var result = new List<ConvertedFile>();
        for (uint i = 0; i < pdf.PageCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var page = pdf.GetPage(i);
            var width = Math.Ceiling(page.Size.Width * request.Options.PdfDpi / 96.0);
            var height = Math.Ceiling(page.Size.Height * request.Options.PdfDpi / 96.0);
            CheckSize(width, height);
            using var rendered = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(rendered, new PdfPageRenderOptions { DestinationWidth = (uint)width, DestinationHeight = (uint)height })
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (rendered.Size > 256 * 1024 * 1024) throw new IOException("PDF 页面图片过大，请降低分辨率。");
            using var reader = new DataReader(rendered.GetInputStreamAt(0));
            await reader.LoadAsync((uint)rendered.Size).AsTask(cancellationToken).ConfigureAwait(false);
            var bytes = new byte[(int)rendered.Size]; reader.ReadBytes(bytes);
            using var memory = new MemoryStream(bytes, writable: false);
            var image = BitmapDecoder.Create(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var path = Path.Combine(workspace, $"page-{i + 1}." + request.Target);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                SaveImage(image, request.Target, request.Options.Quality, output, cancellationToken);
            result.Add(new(path, Path.GetFileNameWithoutExtension(request.Source) + $"-{i + 1}." + request.Target));
            progress?.Report((i + 1) * 100.0 / pdf.PageCount);
        }
        return result;
    }

    internal static async Task<IReadOnlyList<ConvertedFile>> ConvertMediaInWorkerAsync(ConversionRequest request, string workspace,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var profile = request.Target switch
        {
            "mp3" => MediaEncodingProfile.CreateMp3(AudioEncodingQuality.High),
            "m4a" => MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High),
            "wav" => MediaEncodingProfile.CreateWav(AudioEncodingQuality.High),
            "mp4" => MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p),
            "wmv" => MediaEncodingProfile.CreateWmv(VideoEncodingQuality.HD720p),
            _ => throw new InvalidOperationException("此格式需要本地 FFmpeg，请在本地引擎中选择程序。")
        };
        var input = await StorageFile.GetFileFromPathAsync(request.Source).AsTask(cancellationToken).ConfigureAwait(false);
        using var inputStream = await input.OpenAsync(FileAccessMode.Read).AsTask(cancellationToken).ConfigureAwait(false);
        // Preserve source video dimensions and frame rate; do not silently force 720p.
        if (ConversionFormats.IsVideoTarget(request.Target))
        {
            var original = await MediaEncodingProfile.CreateFromStreamAsync(inputStream).AsTask(cancellationToken).ConfigureAwait(false);
            if (original.Video is null || original.Video.Width == 0) throw new InvalidOperationException("原文件没有可转换的视频轨道。");
            profile.Video.Width = original.Video.Width; profile.Video.Height = original.Video.Height;
            profile.Video.FrameRate.Numerator = original.Video.FrameRate.Numerator;
            profile.Video.FrameRate.Denominator = original.Video.FrameRate.Denominator;
            profile.Video.Bitrate = original.Video.Bitrate > 0 ? original.Video.Bitrate : 8_000_000;
            inputStream.Seek(0);
        }
        var folder = await StorageFolder.GetFolderFromPathAsync(workspace).AsTask(cancellationToken).ConfigureAwait(false);
        var output = await folder.CreateFileAsync("converted." + request.Target, CreationCollisionOption.FailIfExists).AsTask(cancellationToken).ConfigureAwait(false);
        using var outputStream = await output.OpenAsync(FileAccessMode.ReadWrite).AsTask(cancellationToken).ConfigureAwait(false);
        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = false };
        // Own both streams explicitly: the file-based overload can retain its
        // private file handles after the completion callback until WinRT GC.
        var prepared = await transcoder.PrepareStreamTranscodeAsync(inputStream, outputStream, profile).AsTask(cancellationToken).ConfigureAwait(false);
        if (!prepared.CanTranscode) throw new InvalidOperationException($"系统不支持此编解码组合（{prepared.FailureReason}），可选择本地 FFmpeg 重试。");
        await prepared.TranscodeAsync().AsTask(cancellationToken, progress).ConfigureAwait(false);
        await outputStream.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
        return [new(Path.Combine(workspace, "converted." + request.Target), Path.GetFileNameWithoutExtension(request.Source) + "." + request.Target)];
    }

    private static BitmapSource Orient(BitmapFrame frame)
    {
        CheckSize(frame.PixelWidth, frame.PixelHeight);
        ushort orientation = 1;
        try
        {
            if (frame.Metadata is BitmapMetadata metadata)
                orientation = Convert.ToUInt16(metadata.GetQuery("/app1/ifd/{ushort=274}") ?? metadata.GetQuery("/ifd/{ushort=274}") ?? (ushort)1);
        }
        catch (Exception e) when (e is NotSupportedException or COMException or ArgumentException) { }
        var matrix = orientation switch
        {
            2 => new Matrix(-1, 0, 0, 1, 0, 0), 3 => new Matrix(-1, 0, 0, -1, 0, 0),
            4 => new Matrix(1, 0, 0, -1, 0, 0), 5 => new Matrix(0, 1, 1, 0, 0, 0),
            6 => new Matrix(0, 1, -1, 0, 0, 0), 7 => new Matrix(0, -1, -1, 0, 0, 0),
            8 => new Matrix(0, -1, 1, 0, 0, 0), _ => Matrix.Identity
        };
        return matrix.IsIdentity ? frame : new TransformedBitmap(frame, new MatrixTransform(matrix));
    }

    private static void SaveImage(BitmapSource image, string format, int quality, Stream output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BitmapEncoder encoder = format switch
        {
            "png" => new PngBitmapEncoder(), "jpg" => new JpegBitmapEncoder { QualityLevel = quality },
            "bmp" => new BmpBitmapEncoder(), "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Zip },
            _ => throw new InvalidOperationException("不支持此图片格式。")
        };
        if (format is "jpg" or "bmp") image = FlattenOnWhite(image, cancellationToken);
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(output);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void WriteRgbRows(BitmapSource image, Stream output, CancellationToken cancellationToken)
    {
        var rgba = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var source = new byte[checked(image.PixelWidth * 4)]; var rgb = new byte[checked(image.PixelWidth * 3)];
        for (var y = 0; y < image.PixelHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rgba.CopyPixels(new System.Windows.Int32Rect(0, y, image.PixelWidth, 1), source, source.Length, 0);
            for (var x = 0; x < image.PixelWidth; x++)
            {
                var a = source[x * 4 + 3];
                for (var c = 0; c < 3; c++) rgb[x * 3 + c] = (byte)((source[x * 4 + 2 - c] * a + 255 * (255 - a) + 127) / 255);
            }
            output.Write(rgb);
        }
    }

    private static BitmapSource FlattenOnWhite(BitmapSource image, CancellationToken cancellationToken)
    {
        var rgba = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = checked(image.PixelWidth * 4);
        var pixels = new byte[checked(stride * image.PixelHeight)];
        rgba.CopyPixels(pixels, stride, 0);
        for (var row = 0; row < image.PixelHeight; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = row * stride; x < (row + 1) * stride; x += 4)
            {
                var a = pixels[x + 3];
                for (var c = 0; c < 3; c++) pixels[x + c] = (byte)((pixels[x + c] * a + 255 * (255 - a) + 127) / 255);
                pixels[x + 3] = 255;
            }
        }
        return new FormatConvertedBitmap(BitmapSource.Create(image.PixelWidth, image.PixelHeight, image.DpiX, image.DpiY,
            PixelFormats.Bgra32, null, pixels, stride), PixelFormats.Bgr24, null, 0);
    }

    private static void CheckSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1 || width * height > MaximumPixels)
            throw new IOException("单页超过 4000 万像素，请缩小图片或降低 PDF 分辨率。");
    }

    private static void ValidateStaticWebP(string path)
    {
        if (!Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase)) return;
        using var input = File.OpenRead(path);
        using var reader = new BinaryReader(input);
        if (input.Length < 12 || new string(reader.ReadChars(4)) != "RIFF") throw new IOException("WebP 文件格式无效。");
        input.Position = 8;
        if (new string(reader.ReadChars(4)) != "WEBP") throw new IOException("WebP 文件格式无效。");
        while (input.Position + 8 <= input.Length)
        {
            var chunk = new string(reader.ReadChars(4)); var size = reader.ReadUInt32();
            if (chunk is "ANIM" or "ANMF") throw new InvalidOperationException("暂不转换动态图，请选择静态图片。");
            if (input.Position + size > input.Length) throw new IOException("WebP 文件不完整。");
            var next = input.Position + size + (size & 1);
            if (chunk == "VP8X" && size >= 10)
            {
                if ((reader.ReadByte() & 2) != 0) throw new InvalidOperationException("暂不转换动态图，请选择静态图片。");
                reader.ReadBytes(3);
                CheckSize(1 + Read24(reader), 1 + Read24(reader));
            }
            else if (chunk == "VP8L" && size >= 5)
            {
                reader.ReadByte(); var dimensions = reader.ReadUInt32();
                CheckSize(1 + (dimensions & 0x3fff), 1 + ((dimensions >> 14) & 0x3fff));
            }
            else if (chunk == "VP8 " && size >= 10)
            {
                reader.ReadBytes(6); CheckSize(reader.ReadUInt16() & 0x3fff, reader.ReadUInt16() & 0x3fff);
            }
            input.Position = next;
        }
    }

    private static int Read24(BinaryReader reader) => reader.ReadByte() | reader.ReadByte() << 8 | reader.ReadByte() << 16;
}
