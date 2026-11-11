using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace DailyToolkit.Core.Conversion;

// A small image-only PDF writer: no scripts, forms, remote resources or fonts.
// Image payloads are supplied by the local image decoder, one page at a time.
public sealed class ImagePdfWriter
{
    private readonly Stream _stream;
    private readonly Dictionary<int, long> _offsets = [];
    private readonly List<int> _pages = [];
    private bool _completed;

    public ImagePdfWriter(Stream stream)
    {
        if (!stream.CanWrite || !stream.CanSeek || stream.Position != 0 || stream.Length != 0) throw new ArgumentException("PDF 输出流必须可写、可定位且为空。", nameof(stream));
        _stream = stream;
        Write("%PDF-1.4\n%DailyToolkit\n");
    }

    public void AddPage(byte[] jpeg, int width, int height, double dpiX = 96, double dpiY = 96)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xff || jpeg[1] != 0xd8) throw new InvalidOperationException("JPEG 数据无效。");
        AddEncodedPage(jpeg, width, height, dpiX, dpiY, "DCTDecode");
    }

    public void AddRgbPage(int width, int height, Action<Stream> writeRgb, double dpiX = 96, double dpiY = 96)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) writeRgb(zlib);
        AddEncodedPage(compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)), width, height, dpiX, dpiY, "FlateDecode");
    }

    private void AddEncodedPage(ReadOnlySpan<byte> encoded, int width, int height, double dpiX, double dpiY, string filter)
    {
        if (_completed || _pages.Count >= 500 || width <= 0 || height <= 0) throw new InvalidOperationException("PDF 图片页无效或超过 500 页。");
        var page = 3 + _pages.Count * 3;
        _pages.Add(page);
        var w = width * 72.0 / (double.IsFinite(dpiX) && dpiX > 0 ? dpiX : 96);
        var h = height * 72.0 / (double.IsFinite(dpiY) && dpiY > 0 ? dpiY : 96);
        var size = FormattableString.Invariant($"{w:0.###} 0 0 {h:0.###} 0 0");
        Object(page, FormattableString.Invariant($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w:0.###} {h:0.###}] /Resources << /XObject << /Im0 {page + 1} 0 R >> >> /Contents {page + 2} 0 R >>\n"));
        StartObject(page + 1);
        Write($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /{filter} /Length {encoded.Length} >>\nstream\n");
        _stream.Write(encoded);
        Write("\nendstream\nendobj\n");
        var content = $"q\n{size} cm\n/Im0 Do\nQ\n";
        Object(page + 2, $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream\n");
    }

    public void Complete()
    {
        if (_completed || _pages.Count == 0) throw new InvalidOperationException("PDF 没有图片页或已完成。");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>\n");
        Object(2, $"<< /Type /Pages /Count {_pages.Count} /Kids [{string.Join(' ', _pages.Select(p => $"{p} 0 R"))}] >>\n");
        var xref = _stream.Position;
        var count = 3 + _pages.Count * 3;
        Write($"xref\n0 {count}\n0000000000 65535 f \n");
        for (var i = 1; i < count; i++) Write(_offsets[i].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        _completed = true;
    }

    private void StartObject(int id) { _offsets.Add(id, _stream.Position); Write($"{id} 0 obj\n"); }
    private void Object(int id, string value) { StartObject(id); Write(value); Write("endobj\n"); }
    private void Write(string value) => _stream.Write(Encoding.ASCII.GetBytes(value));
}
