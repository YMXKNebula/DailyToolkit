using System.Buffers.Binary;

namespace DailyToolkit.Core.Conversion;

public static class ImageInputPolicy
{
    // WIC can expose only the default image of APNG. Reject its animation
    // control chunk before decoding, rather than silently discarding frames.
    public static void RejectAnimatedPng(Stream stream, CancellationToken cancellationToken = default)
    {
        var position = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[8];
            if (stream.Read(header) != 8 || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return;
            while (stream.Position + 8 <= stream.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.ReadExactly(header);
                var size = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (header[4..].SequenceEqual("acTL"u8)) throw new InvalidOperationException("暂不转换 APNG 动图，请选择静态图片。");
                if (stream.Position + size + 4 > stream.Length) throw new IOException("PNG 文件不完整。");
                stream.Position += size + 4L;
            }
        }
        finally { stream.Position = position; }
    }
}
