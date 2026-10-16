using System.IO;
using Microsoft.Win32;

namespace DailyToolkit.Desktop.FilePack;

internal sealed class FilePackContextMenuRegistration
{
    private const string Verb = "DailyToolkit.FilePack";
    private readonly IFilePackMenuRegistry _registry;
    private readonly string _classesPath, _executable;
    internal FilePackContextMenuRegistration(IFilePackMenuRegistry? registry = null, string classesPath = @"Software\Classes", string? executable = null)
    { _registry = registry ?? new FilePackMenuRegistry(); _classesPath = classesPath; _executable = Path.GetFullPath(executable ?? System.Environment.ProcessPath!); }
    private IEnumerable<string> Paths => new[] { "*", "Directory" }.Select(type => $@"{_classesPath}\{type}\shell\{Verb}");
    internal bool IsRegistered => Paths.Any(path => _registry.Read(path)?.GetValueOrDefault("DailyToolkitOwner") == Verb);
    private string Command => $"\"{_executable}\" --file-pack-load \"%1\"";
    internal void SetEnabled(bool enabled)
    {
        if (enabled && (!File.Exists(_executable) || _executable.Contains('"'))) throw new IOException("软件路径无效。");
        // Never delete or overwrite a same-named entry belonging to somebody else.
        var previous = Paths.ToDictionary(path => path, path => (_registry.Read(path), _registry.Read(path + @"\command")));
        foreach (var path in Paths)
        {
            var existing = previous[path].Item1;
            if (enabled && existing is not null && existing.GetValueOrDefault("DailyToolkitOwner") != Verb)
                throw new IOException("右键菜单名称已被其他配置占用。");
        }
        var changed = new List<string>();
        try
        {
            foreach (var path in Paths)
            {
                if (!enabled)
                { if (previous[path].Item1?.GetValueOrDefault("DailyToolkitOwner") == Verb) _registry.Delete(path); continue; }
                changed.Add(path);
                _registry.Write(path, new() { ["DailyToolkitOwner"] = Verb, [""] = "装入文件集装箱",
                    ["Icon"] = $"\"{_executable}\",0", ["MultiSelectModel"] = "Player" });
                _registry.Write(path + @"\command", new() { [""] = Command });
            }
        }
        catch
        {
            // Restore entries already changed by this operation if a later write fails.
            // If permissions also prevent rollback, the owner marker still allows explicit removal.
            foreach (var path in changed.AsEnumerable().Reverse())
                try
                {
                    if (previous[path].Item1 is { } values) _registry.Write(path, values); else { _registry.Delete(path); continue; }
                    if (previous[path].Item2 is { } command) _registry.Write(path + @"\command", command);
                    else _registry.Delete(path + @"\command");
                }
                catch (Exception exception) when (FilePackViewModel.IsExpected(exception)) { }
            throw;
        }
    }
}

internal interface IFilePackMenuRegistry
{
    Dictionary<string, string>? Read(string path);
    void Write(string path, Dictionary<string, string> values);
    void Delete(string path);
}

internal sealed class FilePackMenuRegistry : IFilePackMenuRegistry
{
    public Dictionary<string, string>? Read(string path)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValueNames().Where(name => key.GetValue(name) is string).ToDictionary(name => name, name => (string)key.GetValue(name)!);
    }
    public void Write(string path, Dictionary<string, string> values)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
        foreach (var value in values) key.SetValue(value.Key, value.Value);
    }
    public void Delete(string path) => Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
}
