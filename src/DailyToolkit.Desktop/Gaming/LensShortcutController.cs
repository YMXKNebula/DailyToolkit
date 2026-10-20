using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Runtime;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Gaming;

internal sealed class LensShortcutController : IDisposable
{
    internal const int HotkeyId = 0xB011;
    private readonly WindowMessageSource _source;
    private readonly int _hotkeyId;
    private readonly Func<uint, bool> _keyDown;
    private readonly DispatcherTimer _releaseTimer;
    private KeyboardShortcut? _shortcut;
    private LensActivationMode _mode;
    private bool _holding, _suspended, _disposed, _hookAttached;
    internal bool HasMessageHook => _hookAttached;
    internal bool IsReleasePolling => _releaseTimer.IsEnabled;

    public LensShortcutController(WindowMessageSource source, Func<uint, bool>? keyDown = null, int hotkeyId = HotkeyId)
    {
        _source = source;
        _hotkeyId = hotkeyId;
        _keyDown = keyDown ?? (key => GetAsyncKeyState((int)key) < 0);
        _releaseTimer = new DispatcherTimer(DispatcherPriority.Input, source.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _releaseTimer.Tick += (_, _) => CheckRelease();
    }

    public event Action? ToggleRequested;
    public event Action? StartRequested;
    public event Action? StopRequested;
    internal event Action<KeyboardShortcut?, ShortcutRegistrationState, int>? RegistrationChanged;
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
        if (_suspended || _shortcut is null)
        { Report(_suspended ? ShortcutRegistrationState.Paused : ShortcutRegistrationState.NotRegistered); return true; }
        if (!_shortcut.IsValid || _mode is not (LensActivationMode.Toggle or LensActivationMode.Hold))
        { Report(ShortcutRegistrationState.Failed); return false; }
        IsRegistered = RegisterHotKey(_source.Handle, _hotkeyId, _shortcut.Modifiers | 0x4000, _shortcut.VirtualKey);
        var error = IsRegistered ? 0 : Marshal.GetLastWin32Error();
        if (IsRegistered) { _source.AddHook(WindowMessage); _hookAttached = true; }
        Report(IsRegistered ? ShortcutRegistrationState.Registered : ShortcutRegistrationState.Failed, error);
        return IsRegistered;
    }

    private void Report(ShortcutRegistrationState state, int error = 0) => RegistrationChanged?.Invoke(_shortcut, state, error);

    private void Unregister()
    {
        if (IsRegistered) UnregisterHotKey(_source.Handle, _hotkeyId);
        IsRegistered = false;
        if (_hookAttached) { _source.RemoveHook(WindowMessage); _hookAttached = false; }
    }

    private IntPtr WindowMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312 || wParam.ToInt32() != _hotkeyId) return IntPtr.Zero;
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
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
