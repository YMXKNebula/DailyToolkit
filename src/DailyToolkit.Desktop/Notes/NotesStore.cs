using System.IO;
using System.Text;
using System.Text.Json;
using DailyToolkit.Core.Notes;

namespace DailyToolkit.Desktop.Notes;

// The document ID gives future notes separate text files without changing this note's format.
public sealed record NotesDocument(string Id, string Title, string Text, string? Category = null);
public sealed record NotesLoadResult(NotesDocument Document, NotesPreferences Preferences, string Notice,
    bool ContentWritable = true, bool SettingsWritable = true);

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
        var text = ""; var preferences = new NotesPreferences(); var notices = new List<string>();
        var contentWritable = true; var settingsWritable = true;
        try { if (File.Exists(ContentPath)) text = await File.ReadAllTextAsync(ContentPath, Utf8).ConfigureAwait(false); }
        catch (Exception exception) when (IsStorageError(exception))
        { contentWritable = false; notices.Add("笔记未能读取，原文件已保留。请打开数据目录检查 default.txt 或其 .bak 备份。"); }
        try
        {
            if (File.Exists(SettingsPath))
            {
                preferences = JsonSerializer.Deserialize<NotesPreferences>(await File.ReadAllTextAsync(SettingsPath, Utf8).ConfigureAwait(false))
                    ?? throw new JsonException();
                if (preferences.SchemaVersion > 1) throw new JsonException();
                preferences = preferences.Normalize();
            }
        }
        catch (Exception exception) when (IsStorageError(exception))
        { settingsWritable = false; notices.Add("浮笺设置未能读取，已临时使用默认值；原文件保留。恢复全部默认设置后可重新保存。"); }
        return new(new("default", "浮笺", text), preferences, string.Join(" ", notices), contentWritable, settingsWritable);
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
