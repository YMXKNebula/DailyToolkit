using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DailyToolkit.Core.Clipboard;

namespace DailyToolkit.Desktop.ClipRecall;

internal sealed class ClipboardImageLimitException : IOException
{
    internal ClipboardImageLimitException() : base("图片超过存储或解码大小限制，未记录。") { }
}
internal sealed class ClipboardImageFormatException : IOException
{
    internal ClipboardImageFormatException() : base("当前图片编码或颜色格式尚未支持，已安全忽略。") { }
}

// All codecs run off UI. A bounded normalized PNG is persisted; hashing uses
// dimensions + straight BGRA pixels, independently of source PNG metadata/DPI.
internal static class ClipboardImageCodec
{
    internal const long AbsolutePixelBytes = 256L * 1024 * 1024;
    internal static long PixelLimit(int maximumMegabytes) => Math.Min(AbsolutePixelBytes, maximumMegabytes * 8L * 1024 * 1024);
    internal static void ValidateDimensions(int width, int height, long maximum)
    {
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384 || (long)width * height * 4 > maximum)
            throw new ClipboardImageLimitException();
    }
    private static BitmapSource ReadPng(byte[] png, int maximumMegabytes)
    {
        if (png.LongLength > maximumMegabytes * 1024L * 1024) throw new ClipboardImageLimitException();
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)) != 13 || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new ClipboardImageFormatException();
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (width > int.MaxValue || height > int.MaxValue) throw new ClipboardImageLimitException();
        ValidateDimensions((int)width, (int)height, PixelLimit(maximumMegabytes));
        using var stream = new MemoryStream(png, writable: false);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new ClipboardImageFormatException();
        var frame = decoder.Frames[0];
        if (frame.PixelWidth != (int)width || frame.PixelHeight != (int)height) throw new ClipboardImageFormatException();
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0); converted.Freeze(); return converted;
    }
    internal static byte[] Pixels(BitmapSource image)
    {
        var pixels = new byte[checked(image.PixelWidth * image.PixelHeight * 4)];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0); return pixels;
    }
    private static byte[] Encode(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static ClipboardContent Normalize(int width, int height, byte[] pixels, int maximumMegabytes)
    {
        ValidateDimensions(width, height, PixelLimit(maximumMegabytes));
        if (pixels.Length != (long)width * height * 4) throw new ClipboardImageFormatException();
        // Invisible RGB channels do not distinguish transparent pixels.
        for (var index = 0; index < pixels.Length; index += 4)
            if (pixels[index + 3] == 0) pixels.AsSpan(index, 3).Clear();
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); source.Freeze();
        var png = Encode(source);
        if (png.LongLength > maximumMegabytes * 1024L * 1024) throw new ClipboardImageLimitException();
        return ClipboardContent.FromImage(png, width, height, Convert.ToHexString(SHA256.HashData(pixels)));
    }
    internal static ClipboardContent FromPixels(int width, int height, byte[] pixels, int maximumMegabytes) =>
        Normalize(width, height, pixels, maximumMegabytes);
    internal static ClipboardContent FromPng(byte[] png, int maximumMegabytes)
    {
        var image = ReadPng(png, maximumMegabytes); return Normalize(image.PixelWidth, image.PixelHeight, Pixels(image), maximumMegabytes);
    }
    internal static ClipboardContent FromDib(byte[] dib, int maximumMegabytes)
    {
        if (dib.Length < 40) throw new ClipboardImageFormatException();
        var header = BitConverter.ToUInt32(dib, 0);
        if (header is not (40 or 108 or 124) || header > dib.Length) throw new ClipboardImageFormatException();
        var width = BitConverter.ToInt32(dib, 4); var signedHeight = BitConverter.ToInt32(dib, 8);
        if (signedHeight == int.MinValue) throw new ClipboardImageLimitException();
        var height = Math.Abs(signedHeight); ValidateDimensions(width, height, PixelLimit(maximumMegabytes));
        var bits = BitConverter.ToUInt16(dib, 14); var compression = BitConverter.ToUInt32(dib, 16);
        if (BitConverter.ToUInt16(dib, 12) != 1 || bits is not (24 or 32) || compression is not (0 or 3) ||
            bits == 24 && compression != 0) throw new ClipboardImageFormatException();
        var offset = checked((int)header); var alpha = false;
        if (compression == 3)
        {
            if (dib.Length < 52) throw new ClipboardImageFormatException();
            if (BitConverter.ToUInt32(dib, 40) != 0x00FF0000 || BitConverter.ToUInt32(dib, 44) != 0x0000FF00 ||
                BitConverter.ToUInt32(dib, 48) != 0x000000FF) throw new ClipboardImageFormatException();
            if (header == 40) offset += 12;
            else
            {
                var mask = BitConverter.ToUInt32(dib, 52);
                if (mask is not (0 or 0xFF000000)) throw new ClipboardImageFormatException();
                alpha = mask != 0;
            }
        }
        var colors = BitConverter.ToUInt32(dib, 32);
        if (colors > 256) throw new ClipboardImageFormatException();
        offset = checked(offset + (int)colors * 4);
        // Color-profile payloads have nontrivial offsets; do not guess them.
        if (header == 124 && (BitConverter.ToUInt32(dib, 112) != 0 || BitConverter.ToUInt32(dib, 116) != 0))
            throw new ClipboardImageFormatException();
        var stride = checked((width * bits / 8 + 3) & ~3);
        if ((long)offset + (long)stride * height > dib.LongLength) throw new ClipboardImageFormatException();
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var row = offset + (signedHeight < 0 ? y : height - 1 - y) * stride;
            for (var x = 0; x < width; x++)
            {
                var input = row + x * bits / 8; var output = (y * width + x) * 4;
                dib.AsSpan(input, 3).CopyTo(pixels.AsSpan(output, 3)); pixels[output + 3] = alpha ? dib[input + 3] : (byte)255;
            }
        }
        return Normalize(width, height, pixels, maximumMegabytes);
    }
    internal static BitmapSource Decode(ClipboardContent content) =>
        content.BinaryFormat == "png" && content.Binary is { Length: > 0 } png ? ReadPng(png, 256) : throw new ClipboardImageFormatException();
    internal static bool SamePixels(ClipboardContent left, ClipboardContent right)
    {
        if (left.ImageWidth != right.ImageWidth || left.ImageHeight != right.ImageHeight) return false;
        if (left.Binary is null || right.Binary is null) return false;
        if (left.Binary.AsSpan().SequenceEqual(right.Binary)) return true;
        return Pixels(Decode(left)).AsSpan().SequenceEqual(Pixels(Decode(right)));
    }
    internal static byte[] Thumbnail(ClipboardContent content)
    {
        var image = Decode(content);
        var scale = Math.Min(1, 160d / Math.Max(image.PixelWidth, image.PixelHeight));
        var small = scale < 1 ? new TransformedBitmap(image, new ScaleTransform(scale, scale)) : image;
        small.Freeze(); return Encode(small);
    }
    internal static BitmapSource Preview(byte[] png, int maximumWidth = 900)
    {
        if (png.Length < 33) throw new ClipboardImageFormatException();
        var width = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        var height = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        if (width > int.MaxValue || height > int.MaxValue) throw new ClipboardImageLimitException();
        ValidateDimensions((int)width, (int)height, AbsolutePixelBytes);
        using var stream = new MemoryStream(png, writable: false);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = Math.Max(1, (int)(width * Math.Min(1d, maximumWidth / (double)Math.Max(width, height))));
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    internal static byte[] Dib(ClipboardContent content, bool v5)
    {
        var image = Decode(content); var pixels = Pixels(image); var header = v5 ? 124 : 40;
        var result = new byte[header + pixels.Length];
        BitConverter.GetBytes(header).CopyTo(result, 0); BitConverter.GetBytes(image.PixelWidth).CopyTo(result, 4);
        BitConverter.GetBytes(-image.PixelHeight).CopyTo(result, 8); BitConverter.GetBytes((ushort)1).CopyTo(result, 12);
        BitConverter.GetBytes((ushort)32).CopyTo(result, 14); BitConverter.GetBytes(v5 ? 3u : 0u).CopyTo(result, 16);
        BitConverter.GetBytes(pixels.Length).CopyTo(result, 20);
        if (v5)
        {
            BitConverter.GetBytes(0x00FF0000u).CopyTo(result, 40); BitConverter.GetBytes(0x0000FF00u).CopyTo(result, 44);
            BitConverter.GetBytes(0x000000FFu).CopyTo(result, 48); BitConverter.GetBytes(0xFF000000u).CopyTo(result, 52);
            BitConverter.GetBytes(0x73524742u).CopyTo(result, 56); // LCS_sRGB
        }
        pixels.CopyTo(result, header); return result;
    }
}
