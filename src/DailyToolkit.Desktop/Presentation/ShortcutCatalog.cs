using System.Collections.ObjectModel;
using System.ComponentModel;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Notes;

namespace DailyToolkit.Desktop.Presentation;

internal static class ShortcutIds
{
    internal const string LensToggle = "lens-toggle", NotesToggle = "notes-toggle", NotesFocus = "notes-focus",
        ClipboardToggle = "clipboard-toggle", FilePackToggle = "filepack-toggle";
}

internal enum ShortcutRegistrationState { Unknown, Registered, NotRegistered, Paused, Failed }

public sealed class ShortcutOverviewRow(string id, string tool, string action) : ObservableObject
{
    public string Id { get; } = id;
    public string Tool { get; } = tool;
    public string Action { get; } = action;
    public string ShortcutText => Shortcut?.Name ?? "未设置";
    public string StatusText => !Enabled ? "功能已停用" : Shortcut is null ? "未设置" : Registration switch
    {
        ShortcutRegistrationState.Registered => "已启用",
        ShortcutRegistrationState.NotRegistered => "未启用",
        ShortcutRegistrationState.Paused => "编辑时暂停",
        ShortcutRegistrationState.Failed => "未能启用",
        _ => "等待注册"
    };
    public string Notice { get; private set; } = "";
    public bool HasNotice => Notice.Length > 0;
    internal KeyboardShortcut? Shortcut { get; private set; }
    internal bool Enabled { get; private set; }
    internal ShortcutRegistrationState Registration { get; private set; }
    internal int RegistrationError { get; private set; }

    internal void Configure(KeyboardShortcut? shortcut, bool enabled)
    {
        if (Shortcut != shortcut || Enabled != enabled)
        { Shortcut = shortcut; Enabled = enabled; Registration = ShortcutRegistrationState.Unknown; RegistrationError = 0; }
    }
    internal void Report(ShortcutRegistrationState state, int error)
    { Registration = state; RegistrationError = error; }
    internal void UpdateNotice(string notice)
    { Notice = notice; Notify(""); }
}

public sealed record WindowShortcutRow(string Tool, string Action, string ShortcutText);

// Only model changes and actual registration results refresh these five entries.
// No probing registrations, keyboard hook, timer or enumeration of other apps.
public sealed class ShortcutCatalog : ObservableObject, IDisposable
{
    private readonly MainViewModel _host;
    private readonly Dictionary<string, Action> _detachRegistration = [];
    private readonly Dictionary<string, string> _assignmentNotices = [];
    private readonly ShortcutOverviewRow[] _rows =
    [
        new(ShortcutIds.LensToggle, "放大镜", "开启 / 关闭放大"),
        new(ShortcutIds.NotesToggle, "浮笺", "显示 / 隐藏"),
        new(ShortcutIds.NotesFocus, "浮笺", "聚焦 / 取消聚焦"),
        new(ShortcutIds.ClipboardToggle, "剪贴回溯", "呼出 / 关闭"),
        new(ShortcutIds.FilePackToggle, "文件集装箱", "打开 / 收起窗口")
    ];
    private bool _disposed;
    public ReadOnlyCollection<ShortcutOverviewRow> Rows { get; }
    public IReadOnlyList<WindowShortcutRow> WindowRows { get; } =
    [
        new("浮笺", "退出编辑", "Esc"),
        new("剪贴回溯", "搜索", "Ctrl + F"),
        new("剪贴回溯", "从搜索框进入记录列表", "↑ / ↓"),
        new("剪贴回溯", "复制选中项（备注编辑外）", "Enter"),
        new("剪贴回溯", "删除选中项（文字输入外）", "Delete"),
        new("剪贴回溯", "取消编辑 / 关闭面板", "Esc"),
        new("文件集装箱", "移除选中项（文字输入外）", "Delete"),
        new("文件集装箱", "确认 / 取消路径修改", "Enter / Esc"),
        new("文件集装箱", "收起窗口", "Esc")
    ];
    public string LensNotice => _rows[0].Notice;
    public string NotesToggleNotice => _rows[1].Notice;
    public string NotesFocusNotice => _rows[2].Notice;
    public string ClipboardNotice => _rows[3].Notice;
    public string FilePackNotice => _rows[4].Notice;
    public bool HasLensNotice => _rows[0].HasNotice;
    public bool HasNotesToggleNotice => _rows[1].HasNotice;
    public bool HasNotesFocusNotice => _rows[2].HasNotice;
    public bool HasClipboardNotice => _rows[3].HasNotice;
    public bool HasFilePackNotice => _rows[4].HasNotice;

    internal ShortcutCatalog(MainViewModel host)
    {
        _host = host;
        Rows = Array.AsReadOnly(_rows);
        host.Gaming.PropertyChanged += Changed;
        host.Notes.PropertyChanged += Changed;
        host.ClipRecall.PropertyChanged += Changed;
        host.FilePack.PropertyChanged += Changed;
        Refresh();
    }

    private void Changed(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is "" or "IsEnabled" or "ToggleShortcut" or "Preferences" or "Settings") Refresh();
    }

    private void Refresh()
    {
        _assignmentNotices.Clear();
        _rows[0].Configure(_host.Gaming.ToggleShortcut, _host.Gaming.IsEnabled);
        _rows[1].Configure(_host.Notes.Preferences.ToggleShortcut, _host.Notes.IsEnabled);
        _rows[2].Configure(_host.Notes.Preferences.FocusShortcut, _host.Notes.IsEnabled);
        _rows[3].Configure(_host.ClipRecall.Settings.ToggleShortcut, _host.ClipRecall.IsEnabled);
        _rows[4].Configure(_host.FilePack.ToggleShortcut, _host.FilePack.IsEnabled);
        UpdateNotices();
    }

    private void UpdateNotices()
    {
        foreach (var row in _rows)
        {
            var notices = new List<string>(3);
            if (_assignmentNotices.TryGetValue(row.Id, out var assignment)) notices.Add(assignment);
            if (row.Shortcut is { } shortcut)
            {
                var duplicates = _rows.Where(other => !ReferenceEquals(row, other) && shortcut.Matches(other.Shortcut))
                    .Select(other => $"{other.Tool} · {other.Action}{(!other.Enabled ? "（已停用）" : "")}").ToArray();
                if (duplicates.Length > 0) notices.Add("与" + string.Join("、", duplicates) + "重复。");
                var windows = WindowsShortcutNotice(shortcut);
                if (windows.Length > 0) notices.Add(windows);
                if (row.Enabled && row.Registration == ShortcutRegistrationState.Failed)
                    notices.Add(row.RegistrationError == 1409 ? "快捷键已被占用，请更换组合。" : "快捷键注册失败，请更换组合后重试。");
            }
            row.UpdateNotice(string.Join(" ", notices));
        }
        Notify("");
    }

    internal void ReportAssignmentAttempt(string id, KeyboardShortcut? shortcut, bool accepted)
    {
        if (_disposed) return;
        if (accepted) _assignmentNotices.Remove(id);
        else
        {
            var duplicates = shortcut is null ? Array.Empty<string>() : _rows.Where(other => other.Id != id && shortcut.Matches(other.Shortcut))
                .Select(other => $"{other.Tool} · {other.Action}").ToArray();
            _assignmentNotices[id] = duplicates.Length > 0
                ? "未更改：此组合与" + string.Join("、", duplicates) + "重复。"
                : "快捷键未能保存，请更换组合后重试。";
        }
        UpdateNotices();
    }

    internal void ReportRegistration(string id, KeyboardShortcut? shortcut, ShortcutRegistrationState state, int error = 0)
    {
        if (_disposed) return;
        var row = _rows.First(item => item.Id == id);
        // Ignore a result for an earlier setting superseded while configuring.
        if (shortcut is not null && row.Shortcut != shortcut && row.Enabled) return;
        row.Report(state, error);
        UpdateNotices();
    }

    internal void ObserveRegistration(string id, LensShortcutController controller)
    {
        if (_detachRegistration.Remove(id, out var detach)) detach();
        controller.RegistrationChanged += ChangedRegistration;
        _detachRegistration[id] = () => controller.RegistrationChanged -= ChangedRegistration;
        void ChangedRegistration(KeyboardShortcut? shortcut, ShortcutRegistrationState state, int error) => ReportRegistration(id, shortcut, state, error);
    }

    internal void ObserveRegistration(NotesShortcutController controller)
    {
        foreach (var focus in new[] { false, true })
        {
            var id = focus ? ShortcutIds.NotesFocus : ShortcutIds.NotesToggle;
            if (_detachRegistration.Remove(id, out var detach)) detach();
            controller.RegistrationChanged += ChangedRegistration;
            _detachRegistration[id] = () => controller.RegistrationChanged -= ChangedRegistration;
            void ChangedRegistration(bool isFocus, KeyboardShortcut? shortcut, ShortcutRegistrationState state, int error)
            { if (focus == isFocus) ReportRegistration(id, shortcut, state, error); }
        }
    }

    internal static string WindowsShortcutNotice(KeyboardShortcut shortcut)
    {
        if (shortcut.VirtualKey == 0x7B) return "F12 为 Windows 调试器保留，请更换组合。";
        var action = (shortcut.Modifiers, shortcut.VirtualKey) switch
        {
            (8, 0x4C) => "锁定电脑", (8, 0x44) => "显示桌面", (8, 0x45) => "打开文件资源管理器",
            (8, 0x52) => "打开运行", (8, 0x49) => "打开设置", (8, 0x56) => "打开剪贴板历史",
            (8, 0x53) => "打开搜索", (8, 0x09) => "打开任务视图", (12, 0x53) => "打开截图工具",
            (1, 0x09) or (5, 0x09) => "切换应用", (1, 0x73) => "关闭当前窗口", (1, 0x1B) => "切换窗口",
            (2, 0x1B) => "打开开始菜单", (6, 0x1B) => "打开任务管理器", (3, 0x2E) => "打开安全选项",
            (8, 0x20) => "切换输入语言", (2, 0x20) => "切换中文输入法",
            (0, 0x2C) or (1, 0x2C) => "截屏", _ => ""
        };
        if (action.Length > 0) return "与 Windows“" + action + "”快捷键相同，可能触发系统操作。";
        return (shortcut.Modifiers & 8) != 0 ? "Win 组合键由 Windows 保留，可能触发系统操作。" : "";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Gaming.PropertyChanged -= Changed;
        _host.Notes.PropertyChanged -= Changed;
        _host.ClipRecall.PropertyChanged -= Changed;
        _host.FilePack.PropertyChanged -= Changed;
        foreach (var detach in _detachRegistration.Values) detach();
        _detachRegistration.Clear();
    }
}
