using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DailyToolkit.Core.Conversion;
using DailyToolkit.Desktop.Conversion;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckBundledConversionAsync(string directory)
    {
        var root = ConversionRunner.LocalPath(directory);
        var engines = BundledConversionEngines.Resolve(new(), root);
        BundledConversionEngines.Require(engines.FFmpegPath);
        BundledConversionEngines.Require(engines.LibreOfficePath);
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-offline-bundle-");
        try
        {
            var runner = new ConversionRunner(new LocalConversionBackend(root, true));
            await CheckOfficeConversionAsync(fixture.FullName, runner, engines.LibreOfficePath);
            var output = Path.Combine(fixture.FullName, "输出");
            var png = Path.Combine(fixture.FullName, "透明图片.png");
            var pixels = Enumerable.Range(0, 32 * 16).SelectMany(i => new byte[] { 20, 160, 80, i % 2 == 0 ? (byte)0 : (byte)255 }).ToArray();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(32, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4)));
            using (var file = File.Create(png)) encoder.Save(file);
            var stale = new ConversionOptions { FFmpegPath = "Z:\\removed-computer\\ffmpeg.exe", LibreOfficePath = "Z:\\removed-computer\\soffice.exe" };
            var webp = (await runner.RunAsync(new(png, output, ConversionKind.Image, "webp", stale))).Single();
            var restored = (await runner.RunAsync(new(webp, output, ConversionKind.Image, "png", new()))).Single();
            using (var file = File.OpenRead(restored))
            {
                var frame = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                Require(frame.PixelWidth == 32 && frame.PixelHeight == 16, "Bundled WebP round-trip changed dimensions");
                var rgba = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0); var result = new byte[pixels.Length];
                rgba.CopyPixels(result, 32 * 4, 0);
                Require(Enumerable.Range(0, 32 * 16).All(i => result[i * 4 + 3] == pixels[i * 4 + 3]), "Bundled WebP lost alpha");
            }
            var source = Path.Combine(fixture.FullName, "音频.wav");
            var pcm = Enumerable.Range(0, 44100).SelectMany(i => BitConverter.GetBytes((short)(Math.Sin(i * .1) * 10000))).ToArray();
            using (var file = new BinaryWriter(File.Create(source)))
            {
                file.Write("RIFF"u8.ToArray()); file.Write(36 + pcm.Length); file.Write("WAVEfmt "u8.ToArray()); file.Write(16);
                file.Write((short)1); file.Write((short)1); file.Write(44100); file.Write(88200); file.Write((short)2); file.Write((short)16);
                file.Write("data"u8.ToArray()); file.Write(pcm.Length); file.Write(pcm);
            }
            foreach (var format in new[] { "mp3", "m4a", "wav", "flac", "ogg" })
                Require((await runner.RunAsync(new(source, output, ConversionKind.Media, format, stale))).Count == 1, "Bundled audio conversion failed: " + format);
            var raw = Path.Combine(fixture.FullName, "video.rgb");
            await File.WriteAllBytesAsync(raw, Enumerable.Range(0, 64 * 48 * 3 * 10).Select(i => (byte)(i % 251)).ToArray());
            var video = Path.Combine(fixture.FullName, "视频.mp4");
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            await ExternalConversionBackend.RunOwnedWorkerAsync(engines.FFmpegPath,
                ["-nostdin", "-hide_banner", "-loglevel", "error", "-f", "rawvideo", "-pixel_format", "rgb24", "-video_size", "64x48", "-framerate", "10", "-i", raw,
                    "-i", source, "-shortest", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", video], deadline.Token);
            foreach (var format in new[] { "mp4", "wmv", "mkv", "webm", "mp3" })
                Require((await runner.RunAsync(new(video, output, ConversionKind.Media, format, new()), cancellationToken: deadline.Token)).Count == 1, "Bundled video conversion failed: " + format);
            Require(!Directory.EnumerateDirectories(output).Any(), "Bundled conversion retained staging files");
            foreach (var path in Directory.EnumerateFiles(fixture.FullName, "*", SearchOption.AllDirectories))
            { using var released = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
            Console.WriteLine("PASS Actual packaged engines: empty paths, stale settings, WebP alpha, all audio/video targets, Office families/PDF and exclusive file reopening");
        }
        finally { fixture.Delete(true); }
    }
}
