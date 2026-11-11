namespace DailyToolkit.Core.Conversion;

public static class BundledConversionEngines
{
    // Resolve on use so moving the complete portable folder needs no saved path.
    // Old absolute paths remain readable settings, but never override the bundle.
    public static ConversionOptions Resolve(ConversionOptions options, string? applicationDirectory = null)
    {
        var root = ConversionRunner.LocalPath(applicationDirectory ?? AppContext.BaseDirectory);
        return options with
        {
            FFmpegPath = Path.Combine(root, "tools", "ffmpeg", "ffmpeg.exe"),
            LibreOfficePath = Path.Combine(root, "tools", "libreoffice", "program", "soffice.com")
        };
    }

    public static void Require(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("转换组件缺失，请重新安装或解压完整软件包。", path);
    }
}
