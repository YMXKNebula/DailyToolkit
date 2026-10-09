using System.Runtime.InteropServices;
using System.Windows.Interop;
using DailyToolkit.Core.Gaming;

namespace DailyToolkit.Desktop.Notes;

internal sealed class NotesShortcutController : IDisposable
{
    internal const int ToggleId = 0xB021, FocusId = 0xB022;
    private readonly HwndSource _source;
    private KeyboardShortcut? _toggle, _focus;
    private bool _suspended, _disposed;
    public bool ToggleRegistered { get; private set; }
    public bool FocusRegistered { get; private set; }
    public event Action? ToggleRequested;
    public event Action? FocusRequested;
    public string Error { get; private set; } = "";
    public NotesShortcutController(HwndSource source) { _source = source; source.AddHook(Message); }
    public bool Configure(KeyboardShortcut? toggle, KeyboardShortcut? focus)
    {
        Unregister(); _toggle = toggle; _focus = focus;
        return Register();
    }
    public bool Suspend(bool suspended) { Unregister(); _suspended = suspended; return Register(); }
    private bool Register()
    {
        Error = "";
        if (_disposed || _suspended) return true;
        if (_toggle is { IsValid: false } || _focus is { IsValid: false } || _toggle is not null && _toggle.Matches(_focus))
        { Error = "浮笺快捷键无效或相互冲突，请重新设置。"; return false; }
        if (_toggle is not null) ToggleRegistered = RegisterHotKey(_source.Handle, ToggleId, _toggle.Modifiers | 0x4000, _toggle.VirtualKey);
        if (_focus is not null) FocusRegistered = RegisterHotKey(_source.Handle, FocusId, _focus.Modifiers | 0x4000, _focus.VirtualKey);
        if (_toggle is not null && !ToggleRegistered || _focus is not null && !FocusRegistered)
        { Error = "浮笺快捷键注册失败，可能已被其他程序占用；请更换快捷键。"; return false; }
        return true;
    }
    private void Unregister()
    {
        if (ToggleRegistered) UnregisterHotKey(_source.Handle, ToggleId);
        if (FocusRegistered) UnregisterHotKey(_source.Handle, FocusId);
        ToggleRegistered = FocusRegistered = false;
    }
    private IntPtr Message(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312 || _suspended || _disposed) return IntPtr.Zero;
        var id = wParam.ToInt32();
        var shortcut = id == ToggleId && ToggleRegistered ? _toggle : id == FocusId && FocusRegistered ? _focus : null;
        if (shortcut is null) return IntPtr.Zero;
        handled = true;
        var chord = unchecked((ulong)lParam.ToInt64());
        if (shortcut.Modifiers != (chord & 0xFFFF) || shortcut.VirtualKey != ((chord >> 16) & 0xFFFF)) return IntPtr.Zero;
        if (id == ToggleId) ToggleRequested?.Invoke(); else FocusRequested?.Invoke();
        return IntPtr.Zero;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Unregister(); _source.RemoveHook(Message); }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
}
