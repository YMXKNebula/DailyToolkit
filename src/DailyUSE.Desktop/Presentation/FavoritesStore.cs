using System.IO;
using System.Text.Json;

namespace DailyUSE.Desktop.Presentation;

public sealed class FavoritesStore(string? filePath = null)
{
    private readonly string _path = filePath ?? Path.Combine(System.Environment.GetFolderPath(
        System.Environment.SpecialFolder.LocalApplicationData),"DailyUSE","favorites.json");

    public HashSet<string> Load()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 16384) return [];
            var ids=JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path));
            return ids is not null && Valid(ids) ? new(ids,StringComparer.Ordinal) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    public bool Save(IReadOnlyCollection<string> ids)
    {
        if (!Valid(ids)) return false;
        var temporary=_path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(temporary,JsonSerializer.Serialize(ids.Order(StringComparer.Ordinal)));
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
    private static bool Valid(IReadOnlyCollection<string> ids) => ids.Count <= 128 && ids.All(id =>
        id is { Length: > 0 and <= 64 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'));
}
