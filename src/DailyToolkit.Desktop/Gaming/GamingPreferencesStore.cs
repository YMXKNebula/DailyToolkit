using System.IO;
using System.Text.Json;
using DailyToolkit.Core.Gaming;

namespace DailyToolkit.Desktop.Gaming;

public sealed class GamingPreferencesStore(string? filePath = null,string? localDataDirectory = null)
{
    private readonly string _path = filePath ?? Path.Combine(localDataDirectory ?? System.Environment.GetFolderPath(
        System.Environment.SpecialFolder.LocalApplicationData),"DailyToolkit","gaming.json");
    private readonly string? _legacyPath = filePath is not null ? null : Path.Combine(localDataDirectory ?? System.Environment.GetFolderPath(
        System.Environment.SpecialFolder.LocalApplicationData),"DailyUSE","gaming.json");
    public GamingPreferences Load()
    {
        try
        {
            var path=File.Exists(_path) ? _path : _legacyPath ?? _path;
            if (!File.Exists(path) || new FileInfo(path).Length > 16384) return new();
            var settings = JsonSerializer.Deserialize<GamingPreferences>(File.ReadAllText(path))?.Normalize();
            return settings is { IsValid:true } ? settings : new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { System.Diagnostics.Trace.WriteLine(exception); return new(); }
    }
    public bool Save(GamingPreferences preferences)
    {
        if (!preferences.IsValid) return false;
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temporary,JsonSerializer.Serialize(preferences));
            File.Move(temporary,_path,overwrite:true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine(exception); return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { System.Diagnostics.Trace.WriteLine(exception); }
        }
    }
}
