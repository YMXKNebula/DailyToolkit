using System.IO;
using System.Text;
using System.Text.Json;
using DailyToolkit.Core.Notes;

namespace DailyToolkit.Desktop.Notes;

// The document ID gives future notes separate text files without changing this note's format.
public sealed record NotesDocument(string Id, string Title, string Text, string? Category = null);
public sealed record NotesLoadResult(NotesDocument Document, NotesPreferences Preferences, string Notice,
    bool ContentWritable = true, bool SettingsWritable = true);
internal sealed record NotesContentLoadResult(string Text, bool ContentWritable, string Notice);

public sealed class NotesStore
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string DirectoryPath { get; }
    public string ContentPath => Path.Combine(DirectoryPath, "default.txt");
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

    public NotesStore(string? directory = null) => DirectoryPath = directory ?? Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "DailyToolkit", "Notes");

    public async Task<NotesLoadResult> LoadAsync()
    {
        var content = await LoadContentAsync().ConfigureAwait(false);
        var preferences = new NotesPreferences(); var notices = new List<string>();
        if (content.Notice.Length > 0) notices.Add(content.Notice);
        var settingsWritable = true;
        try
        {
            var loadedPreferences = JsonSerializer.Deserialize<NotesPreferences>(await File.ReadAllTextAsync(SettingsPath, Utf8).ConfigureAwait(false))
                ?? throw new JsonException();
            if (loadedPreferences.SchemaVersion > 1) throw new JsonException();
            preferences = loadedPreferences.Normalize();
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception exception) when (IsStorageError(exception))
        { settingsWritable = false; notices.Add("设置读取失败，暂用默认值。原文件保留，恢复默认设置后可重新保存。"); }
        return new(new("default", "浮笺", content.Text), preferences, string.Join(" ", notices), content.ContentWritable, settingsWritable);
    }

    internal async Task<NotesContentLoadResult> LoadContentAsync()
    {
        try
        {
            var text = await File.ReadAllTextAsync(ContentPath, Utf8).ConfigureAwait(false);
            return new(text, true, "");
        }
        catch (FileNotFoundException) { return new("", true, ""); }
        catch (DirectoryNotFoundException) { return new("", true, ""); }
        catch (Exception exception) when (IsStorageError(exception))
        { return new("", false, "笔记读取失败，请在数据目录检查 default.txt 和 .bak 备份。"); }
    }

    public Task SaveContentAsync(string text) => AtomicWriteAsync(ContentPath, text);
    public Task SaveSettingsAsync(NotesPreferences preferences) => AtomicWriteAsync(SettingsPath,
        JsonSerializer.Serialize(preferences.Normalize(), JsonOptions));

    internal static async Task AtomicWriteAsync(string path, string text)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Directory operations and replacement also stay off the UI thread.
            await Task.Run(async () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    var bytes = Utf8.GetBytes(text);
                    await stream.WriteAsync(bytes).ConfigureAwait(false);
                    await stream.FlushAsync().ConfigureAwait(false);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
                else File.Move(temporary, path);
            }).ConfigureAwait(false);
        }
        finally { if (File.Exists(temporary)) await Task.Run(() => File.Delete(temporary)).ConfigureAwait(false); }
    }

    internal static bool IsStorageError(Exception exception) => exception is IOException or UnauthorizedAccessException
        or JsonException or DecoderFallbackException or EncoderFallbackException or System.Security.SecurityException;
}
