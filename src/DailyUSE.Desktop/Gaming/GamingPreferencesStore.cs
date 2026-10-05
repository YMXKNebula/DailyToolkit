using System.IO;
using System.Text.Json;
using DailyUSE.Core.Gaming;

namespace DailyUSE.Desktop.Gaming;

public sealed class GamingPreferencesStore(string? filePath = null)
{
    private readonly string _path = filePath ?? Path.Combine(System.Environment.GetFolderPath(
        System.Environment.SpecialFolder.LocalApplicationData),"DailyUSE","gaming.json");
    public GamingPreferences Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 16384) return new();
            var settings = JsonSerializer.Deserialize<GamingPreferences>(File.ReadAllText(_path));
            return settings is { IsValid:true } ? settings : new();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return new(); }
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
