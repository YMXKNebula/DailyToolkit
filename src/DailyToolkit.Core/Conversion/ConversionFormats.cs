namespace DailyToolkit.Core.Conversion;

public enum ConversionKind { Image, Pdf, Media, Document }

public sealed record ConversionFormat(string Id, string Name);
public sealed record ConversionCategory(ConversionKind Value, string Name);

public sealed record ConversionOptions
{
    public int Quality { get; init; } = 90;
    public int PdfDpi { get; init; } = 150;
    public string FFmpegPath { get; init; } = "";
    public string LibreOfficePath { get; init; } = "";
    public ConversionOptions Normalize() => this with
    {
        Quality = Math.Clamp(Quality, 1, 100), PdfDpi = Math.Clamp(PdfDpi, 72, 300),
        FFmpegPath = FFmpegPath?.Trim() ?? "", LibreOfficePath = LibreOfficePath?.Trim() ?? ""
    };
}

public static class ConversionFormats
{
    public static IReadOnlyList<ConversionCategory> Categories { get; } =
    [new(ConversionKind.Image, "图片"), new(ConversionKind.Pdf, "PDF"),
        new(ConversionKind.Media, "音视频"), new(ConversionKind.Document, "文档")];
    public static IReadOnlyList<ConversionFormat> Targets(ConversionKind kind) => kind switch
    {
        ConversionKind.Image => [new("png", "PNG"), new("jpg", "JPG"), new("bmp", "BMP"), new("tiff", "TIFF"), new("pdf", "PDF"), new("webp", "WebP")],
        ConversionKind.Pdf => [new("png", "PNG"), new("jpg", "JPG")],
        ConversionKind.Media => [new("mp3", "MP3"), new("m4a", "M4A"), new("wav", "WAV"), new("flac", "FLAC"), new("ogg", "OGG"), new("mp4", "MP4"), new("wmv", "WMV"), new("mkv", "MKV"), new("webm", "WebM")],
        ConversionKind.Document => [new("pdf", "PDF"), new("docx", "Word（DOCX）"), new("odt", "ODT"), new("txt", "TXT"), new("xlsx", "Excel（XLSX）"), new("ods", "ODS"), new("csv", "CSV"), new("pptx", "PowerPoint（PPTX）"), new("odp", "ODP")],
        _ => []
    };

    public static bool Accepts(ConversionKind kind, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return kind switch
        {
            ConversionKind.Image => ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff" or ".gif" or ".webp",
            ConversionKind.Pdf => ext == ".pdf",
            ConversionKind.Media => IsAudio(ext) || ext is ".mp4" or ".m4v" or ".mkv" or ".mov" or ".avi" or ".webm" or ".wmv" or ".mpeg" or ".mpg",
            ConversionKind.Document => ext is ".docx" or ".odt" or ".txt" or ".xlsx" or ".ods" or ".csv" or ".pptx" or ".odp",
            _ => false
        };
    }

    public static bool IsAudio(string extension) => extension.ToLowerInvariant() is ".mp3" or ".m4a" or ".aac" or ".wav" or ".flac" or ".ogg" or ".wma" or ".opus";
    public static bool IsVideoTarget(string target) => target is "mp4" or "wmv" or "mkv" or "webm";

    public static void Validate(ConversionKind kind, string source, string target)
    {
        if (!Accepts(kind, source) || !Targets(kind).Any(f => f.Id == target))
            throw new InvalidOperationException("此类型不支持所选文件或输出格式。");
        if (kind == ConversionKind.Media && IsAudio(Path.GetExtension(source)) && IsVideoTarget(target))
            throw new InvalidOperationException("音频文件请选择音频输出格式。");
        if (kind != ConversionKind.Document || target == "pdf") return;
        var ext = Path.GetExtension(source).ToLowerInvariant();
        var text = ext is ".docx" or ".odt" or ".txt";
        var sheet = ext is ".xlsx" or ".ods" or ".csv";
        if (!(text && target is "docx" or "odt" or "txt") &&
            !(sheet && target is "xlsx" or "ods" or "csv") &&
            !(!text && !sheet && target is "pptx" or "odp"))
            throw new InvalidOperationException("文字、表格和演示文稿请分别选择对应的输出格式。");
    }
}
