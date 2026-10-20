using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop.ClipRecall;

internal sealed class ClipboardMonitor : IDisposable
{
    private readonly WindowMessageSource _source;
    private bool _disposed;
    public bool IsListening { get; private set; }
    public event Action? Updated;
    public ClipboardMonitor(WindowMessageSource source) { _source = source; }
    public void Configure(bool listening)
    {
        if (_disposed || listening == IsListening) return;
        if (listening)
        {
            if (!AddClipboardFormatListener(_source.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用剪贴板监听。");
            _source.AddHook(Message);
        }
        else { RemoveClipboardFormatListener(_source.Handle); _source.RemoveHook(Message); }
        IsListening = listening;
    }
    private IntPtr Message(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x031D && IsListening && !_disposed) Updated?.Invoke();
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (_disposed) return;
        Configure(false); _disposed = true; _source.RemoveHook(Message); Updated = null;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")] private static extern bool RemoveClipboardFormatListener(IntPtr window);
}
