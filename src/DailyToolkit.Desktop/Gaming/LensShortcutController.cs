using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using DailyToolkit.Core.Gaming;

namespace DailyToolkit.Desktop.Gaming;

internal sealed class LensShortcutController : IDisposable
{
    internal const int HotkeyId = 0xB011;
    private readonly HwndSource _source;
    private readonly Func<uint, bool> _keyDown;
    private readonly DispatcherTimer _releaseTimer;
    private KeyboardShortcut? _shortcut;
    private LensActivationMode _mode;
    private bool _holding, _suspended, _disposed;

    public LensShortcutController(HwndSource source, Func<uint, bool>? keyDown = null)
    {
        _source = source;
        _keyDown = keyDown ?? (key => GetAsyncKeyState((int)key) < 0);
        _releaseTimer = new DispatcherTimer(DispatcherPriority.Input, source.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _releaseTimer.Tick += (_, _) => CheckRelease();
        _source.AddHook(WindowMessage);
    }

    public event Action? ToggleRequested;
    public event Action? StartRequested;
    public event Action? StopRequested;
    public bool IsRegistered { get; private set; }

    public bool Configure(KeyboardShortcut? shortcut, LensActivationMode mode)
    {
        if (_disposed) return false;
        ReleaseHold();
        Unregister();
        _shortcut = shortcut;
        _mode = mode;
        return Register();
    }

    public bool Suspend(bool suspended)
    {
        if (_disposed) return false;
        ReleaseHold();
        Unregister();
        _suspended = suspended;
        return Register();
    }

    private bool Register()
    {
        if (_suspended || _shortcut is null) return true;
        if (!_shortcut.IsValid || _mode is not (LensActivationMode.Toggle or LensActivationMode.Hold)) return false;
        IsRegistered = RegisterHotKey(_source.Handle, HotkeyId, _shortcut.Modifiers | 0x4000, _shortcut.VirtualKey);
        return IsRegistered;
    }

    private void Unregister()
    {
        if (IsRegistered) UnregisterHotKey(_source.Handle, HotkeyId);
        IsRegistered = false;
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312 || wParam.ToInt32() != HotkeyId) return IntPtr.Zero;
        handled = true;
        var value = unchecked((ulong)lParam.ToInt64());
        if (!IsRegistered || _suspended || _disposed || _shortcut is null ||
            _shortcut.Modifiers != (value & 0xFFFF) || _shortcut.VirtualKey != ((value >> 16) & 0xFFFF)) return IntPtr.Zero;
        if (_mode == LensActivationMode.Toggle) ToggleRequested?.Invoke();
        else if (!_holding && IsChordDown())
        {
            _holding = true;
            _releaseTimer.Start();
            StartRequested?.Invoke();
            // Capture setup may outlast a short press. Check again before the first frame can show.
            CheckRelease();
        }
        return IntPtr.Zero;
    }

    private bool IsChordDown() => _shortcut is { } shortcut && _keyDown(shortcut.VirtualKey) &&
        ((shortcut.Modifiers & 1) == 0 || _keyDown(0x12)) &&
        ((shortcut.Modifiers & 2) == 0 || _keyDown(0x11)) &&
        ((shortcut.Modifiers & 4) == 0 || _keyDown(0x10)) &&
        ((shortcut.Modifiers & 8) == 0 || _keyDown(0x5B) || _keyDown(0x5C));

    private void CheckRelease()
    {
        if (_holding && !IsChordDown()) ReleaseHold();
    }

    private void ReleaseHold()
    {
        _releaseTimer.Stop();
        if (!_holding) return;
        _holding = false;
        StopRequested?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ReleaseHold();
        Unregister();
        _source.RemoveHook(WindowMessage);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
