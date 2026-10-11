using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DailyToolkit.Core.Clipboard;

public enum ClipboardContentType { Text, Link, Files, Image, RichText }

// Images carry normalized PNG plus pixel identity; file records only carry paths.
public sealed record ClipboardContent(ClipboardContentType Type, string Text, string[] Paths,
    byte[]? Binary = null, string? BinaryFormat = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ImageWidth { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ImageHeight { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageFingerprint { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long ImageEncodedBytes { get; init; }
    public static ClipboardContent FromImage(byte[] png, int width, int height, string pixelFingerprint)
    {
        ArgumentNullException.ThrowIfNull(png);
        ArgumentNullException.ThrowIfNull(pixelFingerprint);
        if (png.Length == 0 || width <= 0 || height <= 0 || pixelFingerprint.Length != 64 ||
            !pixelFingerprint.All(Uri.IsHexDigit)) throw new ArgumentException("图片数据或指纹无效。");
        return new(ClipboardContentType.Image, "", [], png, "png")
        { ImageWidth = width, ImageHeight = height, ImageFingerprint = pixelFingerprint, ImageEncodedBytes = png.LongLength };
    }
    public static ClipboardContent FromText(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var link = Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            uri.Scheme is "http" or "https" or "ftp" or "mailto";
        return new(link ? ClipboardContentType.Link : ClipboardContentType.Text, text, []);
    }

    public static ClipboardContent FromFiles(IEnumerable<string> paths)
    {
        var files = paths.ToArray();
        if (files.Length == 0 || files.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("文件路径列表为空或无效。", nameof(paths));
        return new(ClipboardContentType.Files, "", files);
    }

    public byte[] CanonicalBytes() => Type == ClipboardContentType.Image
        ? JsonSerializer.SerializeToUtf8Bytes(new { Type, ImageWidth, ImageHeight, ImageFingerprint })
        // Keep v1 fingerprints byte-for-byte compatible with existing records.
        : JsonSerializer.SerializeToUtf8Bytes(new { Type, Text, Paths, Binary, BinaryFormat });
    public string StableHash() => Convert.ToHexString(SHA256.HashData(CanonicalBytes()));
    [JsonIgnore]
    public long ContentBytes => Type == ClipboardContentType.Files
        ? Paths.Sum(path => (long)Encoding.UTF8.GetByteCount(path) + 1)
        : Type == ClipboardContentType.Image ? Binary?.LongLength ?? ImageEncodedBytes
        : Binary?.LongLength ?? Encoding.UTF8.GetByteCount(Text);
    public bool SameContent(ClipboardContent other) => Type == other.Type && Text == other.Text &&
        Paths.SequenceEqual(other.Paths, StringComparer.Ordinal) && BinaryFormat == other.BinaryFormat &&
        ImageWidth == other.ImageWidth && ImageHeight == other.ImageHeight && ImageFingerprint == other.ImageFingerprint &&
        (Binary is null && other.Binary is null || Binary is not null && other.Binary is not null && Binary.AsSpan().SequenceEqual(other.Binary));
    public bool Contains(string query) => Text.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Paths.Any(path => path.Contains(query, StringComparison.OrdinalIgnoreCase));
    [JsonIgnore]
    public string DisplayText => Type == ClipboardContentType.Image ? $"图片 · {ImageWidth} × {ImageHeight}" :
        Type == ClipboardContentType.Files ? string.Join("\n", Paths) : Text;
    [JsonIgnore]
    public string Preview
    {
        get
        {
            var text = DisplayText;
            var length = Math.Min(text.Length, 220);
            if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            return text[..length].Replace('\r', ' ').Replace('\n', ' ') + (length < text.Length ? "…" : "");
        }
    }
}

public sealed record ClipboardHistoryRecord(Guid Id, ClipboardContent Content, DateTimeOffset CreatedAt,
    DateTimeOffset? LastCapturedAt, DateTimeOffset? LastUsedAt, bool IsSaved, bool IsPinned,
    string Note, bool IsManual, string? SourceApplication, long StoredBytes);

public sealed record ClipboardHistoryQuery(string Search = "", bool SavedOnly = false,
    ClipboardContentType? Type = null, int Offset = 0, int Limit = 100);
public sealed record ClipboardHistoryEntry(Guid Id, ClipboardContentType Type, string Preview,
    DateTimeOffset CreatedAt, DateTimeOffset? LastCapturedAt, bool IsSaved, bool IsPinned,
    string NotePreview, bool IsManual, string? SourceApplication)
{
    [JsonIgnore]
    public bool IsImage => Type == ClipboardContentType.Image;
    public static ClipboardHistoryEntry FromRecord(ClipboardHistoryRecord record) => new(record.Id, record.Content.Type,
        record.Content.Preview, record.CreatedAt, record.LastCapturedAt, record.IsSaved, record.IsPinned,
        record.Note.Length <= 80 ? record.Note : record.Note[..80] + "…", record.IsManual, record.SourceApplication);
    public string Icon => Type switch { ClipboardContentType.Link => "\uE71B", ClipboardContentType.Files => "\uE8B7",
        ClipboardContentType.Image => "\uEB9F", _ => "\uE8A5" };
    public string TimeText => LastCapturedAt is { } captured ? $"复制于 {captured.ToLocalTime():MM-dd HH:mm:ss}" : $"创建于 {CreatedAt.ToLocalTime():MM-dd HH:mm:ss}";
    public string StateText => string.Join(" · ", new[] { IsSaved ? "永久保存" : "", IsPinned ? "置顶" : "", IsManual ? "手动" : "" }.Where(value => value.Length > 0));
}
public sealed record ClipboardHistoryPage(IReadOnlyList<ClipboardHistoryEntry> Records, int Total);
public sealed record ClipboardHistoryStatistics(int Total, int Saved, int Ordinary, long OrdinaryBytes, long StorageBytes);
public sealed record ClipboardDeleteReceipt(Guid OperationId, int Count, DateTimeOffset ExpiresAt);

public sealed record ClipboardOperationResult(bool Success, string Message = "")
{
    public static ClipboardOperationResult Ok { get; } = new(true);
}
