using System.IO;
using System.Text.Json;

namespace DailyToolkit.Desktop.Presentation;

public sealed record AppPreferences
{
    public bool PageAnimationsEnabled { get; init; } = true;
    public string AnimationColor { get; init; } = "#267A5D";
    public ThemePalette Theme { get; init; } = new();
    public bool CloseToTray { get; init; }
    public bool MinimizeToTray { get; init; }
    public bool StartAtLogin { get; init; }
    public bool AdminStartup { get; init; }
    public bool SilentStartup { get; init; }

    public AppPreferences Normalize() => this with
    {
        AnimationColor=ThemePalette.NormalizeColor(AnimationColor,"#267A5D"),
        Theme=(Theme ?? new()).Normalize(), AdminStartup=StartAtLogin && AdminStartup
    };
}

public sealed class AppPreferencesStore(string? filePath=null)
{
    private readonly string _path=filePath ?? Path.Combine(System.Environment.GetFolderPath(
        System.Environment.SpecialFolder.LocalApplicationData),"DailyToolkit","settings.json");

    public AppPreferences Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 16384) return new();
            return (JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(_path)) ?? new()).Normalize();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { System.Diagnostics.Trace.WriteLine(exception); return new(); }
    }

    public bool Save(AppPreferences preferences)
    {
        var temporary=_path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temporary,JsonSerializer.Serialize(preferences));
            File.Move(temporary,_path,overwrite:true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.WriteLine(exception); return false; }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.WriteLine(exception); }
        }
    }
}
