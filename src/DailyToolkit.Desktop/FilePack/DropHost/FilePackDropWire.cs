using System.IO;
using System.Text.Json;
using System.Text.Encodings.Web;

namespace DailyToolkit.Desktop.FilePack.DropHost;

internal sealed record FilePackDropMessage(string Kind, string[]? Paths = null, long Window = 0, int Id = 0,
    uint Background = 0, uint Foreground = 0, uint Border = 0, bool Enabled = false);

internal static class FilePackDropWire
{
    internal const int MaximumBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static byte[] Encode(FilePackDropMessage message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Options);
        if (bytes.Length is < 2 or > MaximumBytes) throw new InvalidDataException("拖放请求过大。");
        return bytes;
    }

    internal static async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken cancellation)
    {
        if (bytes.Length is < 2 or > MaximumBytes) throw new InvalidDataException("拖放请求大小无效。");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellation).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
        await stream.FlushAsync(cancellation).ConfigureAwait(false);
    }

    internal static async Task<FilePackDropMessage> ReadAsync(Stream stream, CancellationToken cancellation)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        var length = BitConverter.ToInt32(header);
        if (length is < 2 or > MaximumBytes) throw new InvalidDataException("拖放请求大小无效。");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellation).ConfigureAwait(false);
        return JsonSerializer.Deserialize<FilePackDropMessage>(bytes, Options) ?? throw new InvalidDataException("拖放请求无效。");
    }

    internal static bool IsExpected(Exception error) => error is IOException or OperationCanceledException or
        UnauthorizedAccessException or ObjectDisposedException or JsonException or System.ComponentModel.Win32Exception or InvalidOperationException;
}
