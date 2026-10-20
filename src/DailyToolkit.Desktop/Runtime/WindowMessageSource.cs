using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DailyToolkit.Desktop.Runtime;

// Controllers need a message handle, its dispatcher and hooks. They do not need
// the HwndTarget, input providers or render channels created by HwndSource.
internal readonly struct WindowMessageSource
{
    private readonly HwndSource? _wpf;
    private readonly NativeMessageWindow? _native;

    private WindowMessageSource(HwndSource source) => _wpf = source;
    private WindowMessageSource(NativeMessageWindow source) => _native = source;

    internal IntPtr Handle => _native?.Handle ?? Wpf.Handle;
    internal Dispatcher Dispatcher => _native?.Dispatcher ?? Wpf.Dispatcher;
    private HwndSource Wpf => _wpf ?? throw new InvalidOperationException("Window message source is not initialized.");

    internal void AddHook(HwndSourceHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        if (_native is not null) _native.AddHook(hook); else Wpf.AddHook(hook);
    }
    internal void RemoveHook(HwndSourceHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        if (_native is not null) _native.RemoveHook(hook); else Wpf.RemoveHook(hook);
    }

    public static implicit operator WindowMessageSource(HwndSource source) => new(source ?? throw new ArgumentNullException(nameof(source)));
    public static implicit operator WindowMessageSource(NativeMessageWindow source) => new(source ?? throw new ArgumentNullException(nameof(source)));
}

// A real top-level, never-shown HWND: EnumWindows can find the single-instance
// marker, and broadcasts such as TaskbarCreated still arrive. HWND_MESSAGE would
// lose both behaviors. No WPF presentation source or render target is created.
internal sealed class NativeMessageWindow : DispatcherObject, IDisposable
{
    private const uint PopupStyle = 0x80000000, ToolWindowStyle = 0x80;
    private const uint NonClientDestroy = 0x0082;
    private readonly string _className = "DailyToolkit.Message." + Guid.NewGuid().ToString("N");
    private readonly IntPtr _instance;
    private readonly WindowProcedure _procedure;
    private HwndSourceHook? _hooks;
    private bool _classRegistered, _disposing, _disposed;

    // A registered class stores an unmanaged function pointer. Keep that delegate
    // rooted even if construction/disposal fails, until UnregisterClass succeeds.
    private static readonly object ClassGate = new();
    private static readonly Dictionary<string, WindowProcedure> RegisteredProcedures = new(StringComparer.Ordinal);

    internal IntPtr Handle { get; private set; }

    internal NativeMessageWindow(string name = "DailyToolkit.Runtime")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        VerifyAccess();
        _procedure = WindowProc;
        _instance = GetModuleHandle(null);
        if (_instance == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "The message-window module is unavailable.");
        var definition = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = _procedure,
            Instance = _instance, ClassName = _className
        };
        if (RegisterClassEx(ref definition) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The message-window class could not be registered.");
        _classRegistered = true;
        lock (ClassGate) RegisteredProcedures.Add(_className, _procedure);
        try
        {
            // Parent is null and WS_CHILD/WS_VISIBLE are absent. A zero-sized,
            // borderless popup receives messages without participating in display.
            Handle = CreateWindowEx(ToolWindowStyle, _className, name, PopupStyle,
                0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, _instance, IntPtr.Zero);
            if (Handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The hidden message window could not be created.");
        }
        catch (Exception creation)
        {
            try { UnregisterOwnedClass(); }
            catch (Exception cleanup)
            { throw new AggregateException("Message-window creation and class cleanup both failed.", creation, cleanup); }
            throw;
        }
    }

    internal void AddHook(HwndSourceHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed || Handle == IntPtr.Zero, this);
        // Match HwndSource: the most recently added hook runs first.
        _hooks = (HwndSourceHook?)Delegate.Combine(hook, _hooks);
    }

    internal void RemoveHook(HwndSourceHook hook)
    {
        ArgumentNullException.ThrowIfNull(hook);
        VerifyAccess();
        _hooks = (HwndSourceHook?)Delegate.Remove(_hooks, hook);
    }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            var handlers = _hooks?.GetInvocationList();
            if (handlers is not null)
                foreach (HwndSourceHook hook in handlers)
                {
                    var handled = false;
                    var result = hook(window, unchecked((int)message), wParam, lParam, ref handled);
                    if (handled) return result;
                }
            return DefWindowProc(window, message, wParam, lParam);
        }
        catch (Exception exception)
        {
            // Managed exceptions must not escape a native WNDPROC boundary. Keep
            // only the existing bounded diagnostic category; do not log payloads.
            try { DiagnosticReportService.Shared.RecordError(DiagnosticModule.Application, exception); }
            catch (Exception) { /* Diagnostic allocation must not escape WNDPROC either. */ }
            return DefWindowProc(window, message, wParam, lParam);
        }
        finally
        {
            if (message == NonClientDestroy && Handle == window) Handle = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        VerifyAccess();
        if (_disposed || _disposing) return;
        _disposing = true;
        try
        {
            var window = Handle;
            if (window != IntPtr.Zero)
            {
                if (!DestroyWindow(window))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The hidden message window could not be destroyed.");
                Handle = IntPtr.Zero;
            }
            _hooks = null;
            UnregisterOwnedClass();
            _disposed = true;
        }
        finally { _disposing = false; GC.KeepAlive(_procedure); }
    }

    private void UnregisterOwnedClass()
    {
        if (!_classRegistered) return;
        if (!UnregisterClass(_className, _instance))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The message-window class could not be unregistered.");
        _classRegistered = false;
        lock (ClassGate) RegisteredProcedures.Remove(_className);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WindowProcedure(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style;
        public WindowProcedure? Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background;
        public string? MenuName, ClassName;
        public IntPtr SmallIcon;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass definition);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string name, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string name, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}
