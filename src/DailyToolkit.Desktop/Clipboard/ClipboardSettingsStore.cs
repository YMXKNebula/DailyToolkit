using System.Text.Json;
using DailyToolkit.Core.Clipboard;
using DailyToolkit.Desktop.Notes;

namespace DailyToolkit.Desktop.ClipRecall;

public sealed class ClipboardSettingsStore
{
    public string DirectoryPath { get; }
    public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public ClipboardSettingsStore(string? directory = null) => DirectoryPath = directory ?? Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "DailyToolkit", "ClipboardHistory");
    internal async Task<(ClipboardHistorySettings Settings, bool Writable, string Notice)> LoadAsync()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return (new(), true, "");
            if (new FileInfo(SettingsPath).Length > 65536) throw new JsonException();
            var settings = JsonSerializer.Deserialize<ClipboardHistorySettings>(await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false));
            if (settings is null || settings.SchemaVersion != 1) throw new JsonException();
            return (settings.Normalize(), true, "");
        }
        catch (Exception exception) when (NotesStore.IsStorageError(exception))
        { return (new(), false, "剪贴回溯设置未能读取，监听已关闭，原文件保留。请检查数据目录后恢复设置。"); }
    }
    internal Task SaveAsync(ClipboardHistorySettings settings) => NotesStore.AtomicWriteAsync(SettingsPath,
        JsonSerializer.Serialize(settings.Normalize(), new JsonSerializerOptions { WriteIndented = true }));
}
