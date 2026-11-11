using System.IO;
using System.Text.Json;
using DailyToolkit.Core.Conversion;

namespace DailyToolkit.Desktop.Conversion;

public sealed record ConversionSettings
{
    public bool IsEnabled { get; init; }
    public ConversionKind Kind { get; init; }
    public string Target { get; init; } = "png";
    public string OutputDirectory { get; init; } = "";
    public ConversionOptions Options { get; init; } = new();
    public ConversionSettings Normalize()
    {
        var kind = Enum.IsDefined(Kind) ? Kind : ConversionKind.Image;
        return this with { Kind = kind, Target = ConversionFormats.Targets(kind).Any(f => f.Id == Target) ? Target : ConversionFormats.Targets(kind)[0].Id,
            OutputDirectory = OutputDirectory ?? "", Options = (Options ?? new()).Normalize() };
    }
}

public sealed class ConversionSettingsStore(string? path = null)
{
    private readonly string _path = path ?? Path.Combine(System.Environment.GetFolderPath(
        System.Environment.SpecialFolder.LocalApplicationData), "DailyToolkit", "conversion.json");

    public ConversionSettings Load()
    {
        try
        {
            return File.Exists(_path) && new FileInfo(_path).Length <= 16384
                ? (JsonSerializer.Deserialize<ConversionSettings>(File.ReadAllText(_path)) ?? new()).Normalize() : new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { System.Diagnostics.Trace.WriteLine(e); return new(); }
    }

    public bool Save(ConversionSettings settings)
    {
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Normalize()));
            File.Move(temporary, _path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.WriteLine(e); return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine(e); }
        }
    }
}
