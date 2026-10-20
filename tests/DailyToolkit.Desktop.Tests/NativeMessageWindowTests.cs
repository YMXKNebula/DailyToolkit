using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;
using DailyToolkit.Desktop.Notes;
using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task CheckNativeMessageWindowAsync()
    {
        RequireRuntimeFixtureStation();
        await CheckNativeMessageHooksAndShortcutsAsync();
        await CheckNativeRuntimeMessagesAsync();
    }

    private static async Task CheckNativeMessageHooksAndShortcutsAsync()
    {
        const int probe = 0x8000 + 0x161;
        string className;
        var hotkeysUnavailable = false;
        var module = GetNativeFixtureModule(null);
        using (var source = new NativeMessageWindow("DailyToolkit isolated native message fixture"))
        {
            var handle = source.Handle;
            Require(handle != IntPtr.Zero && ReviewIsWindow(handle) && GetNativeFixtureParent(handle) == IntPtr.Zero &&
                NativeFixtureEnumerates(handle) && HwndSource.FromHwnd(handle) is null &&
                ReferenceEquals(source.Dispatcher, Dispatcher.CurrentDispatcher),
                "Native source is not an owned top-level dispatcher HWND without a WPF presentation source");
            var name = new StringBuilder(256);
            Require(GetNativeFixtureClassName(handle, name, name.Capacity) > 0, "Native fixture class name was unavailable");
            className = name.ToString();
            var definition = new NativeFixtureWindowClass { Size = (uint)Marshal.SizeOf<NativeFixtureWindowClass>() };
            Require(GetNativeFixtureClassInfo(module, className, ref definition), "Live native fixture class was not registered");

            var order = new List<string>(); var shortCircuit = false;
            HwndSourceHook first = (IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message != probe) return IntPtr.Zero;
                order.Add("first"); handled = true; return new IntPtr(0x111);
            };
            HwndSourceHook? selfRemoving = null;
            selfRemoving = (IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == probe) { order.Add("self"); source.RemoveHook(selfRemoving!); }
                return IntPtr.Zero;
            };
            HwndSourceHook last = (IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message != probe) return IntPtr.Zero;
                order.Add("last"); handled = shortCircuit; return new IntPtr(0x333);
            };
            source.AddHook(first); source.AddHook(selfRemoving); source.AddHook(last);
            // Assert outside WNDPROC: its exception boundary must not swallow a test failure.
            var result = SendNativeFixtureMessage(handle, probe, IntPtr.Zero, IntPtr.Zero);
            Require(result == new IntPtr(0x111) && order.SequenceEqual(["last", "self", "first"]),
                "Native hooks lost reverse registration order, self-removal safety or the handled return value");
            order.Clear(); result = SendNativeFixtureMessage(handle, probe, IntPtr.Zero, IntPtr.Zero);
            Require(result == new IntPtr(0x111) && order.SequenceEqual(["last", "first"]),
                "A hook that removed itself still ran on the next message");
            order.Clear(); shortCircuit = true;
            result = SendNativeFixtureMessage(handle, probe, IntPtr.Zero, IntPtr.Zero);
            Require(result == new IntPtr(0x333) && order.SequenceEqual(["last"]), "A handled native hook did not stop dispatch");
            source.RemoveHook(last); source.RemoveHook(first);

            var lensChord = new KeyboardShortcut(6, 0x87, "Ctrl + Shift + F24");
            var noteToggle = new KeyboardShortcut(3, 0x86, "Ctrl + Alt + F23");
            var noteFocus = new KeyboardShortcut(7, 0x86, "Ctrl + Alt + Shift + F23");
            using (var lens = new LensShortcutController(source, _ => false, hotkeyId: 0xB091))
            using (var notes = new NotesShortcutController(source))
            {
                var lensCount = 0; var toggleCount = 0; var focusCount = 0;
                lens.ToggleRequested += () => lensCount++;
                notes.ToggleRequested += () => toggleCount++; notes.FocusRequested += () => focusCount++;
                var lensConfigured = lens.Configure(lensChord, LensActivationMode.Toggle);
                var lensError = Marshal.GetLastPInvokeError();
                var notesConfigured = notes.Configure(noteToggle, noteFocus);
                var notesError = Marshal.GetLastPInvokeError();
                var registration = $"lensConfigured={lensConfigured}, lensRegistered={lens.IsRegistered}, lensError={lensError}; " +
                    $"notesConfigured={notesConfigured}, toggleRegistered={notes.ToggleRegistered}, focusRegistered={notes.FocusRegistered}, notesError={notesError}";
                Console.WriteLine("INFO Native fixture hotkey registration: " + registration);
                if (!lensConfigured || !notesConfigured)
                {
                    Require(!lensConfigured && !lens.IsRegistered && !notesConfigured && !notes.ToggleRegistered && !notes.FocusRegistered &&
                        IsNativeFixtureStationDenial(lensError) && IsNativeFixtureStationDenial(notesError),
                        "Native source could not register isolated independent lens/notes shortcuts: " + registration);
                    // Prove the old adapter is denied on this same station too. A
                    // native-only failure must remain a failure, not a platform skip.
                    using var compatibilitySource = new HwndSource(new HwndSourceParameters("DailyToolkit isolated hotkey station probe")
                    { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) });
                    using var compatibility = new LensShortcutController(compatibilitySource, _ => false, hotkeyId: 0xB092);
                    var compatibleConfigured = compatibility.Configure(lensChord, LensActivationMode.Toggle);
                    var compatibleError = Marshal.GetLastPInvokeError();
                    Require(!compatibleConfigured && !compatibility.IsRegistered && IsNativeFixtureStationDenial(compatibleError),
                        $"Pure native shortcut failed while the old HwndSource adapter was not equivalently denied: {registration}; " +
                        $"adapterConfigured={compatibleConfigured}, adapterRegistered={compatibility.IsRegistered}, adapterError={compatibleError}");
                    hotkeysUnavailable = true;
                    Console.WriteLine($"SKIP Native hotkey callback/suspension/registration-reuse assertions: the private noninteractive station denied both native and HwndSource registration; {registration}; adapterError={compatibleError}. No interactive desktop was used.");
                }
                else
                {
                    Require(lens.IsRegistered && notes.ToggleRegistered && notes.FocusRegistered,
                        "Native shortcut Configure succeeded without its expected registrations: " + registration);
                    await PostChordAsync(0xB091, lensChord);
                    await PostChordAsync(NotesShortcutController.ToggleId, noteToggle);
                    await PostChordAsync(NotesShortcutController.FocusId, noteFocus);
                    Require(lensCount == 1 && toggleCount == 1 && focusCount == 1,
                        "Posted WM_HOTKEY did not reach lens/notes callbacks through the pure native source");
                    await PostChordAsync(0xB091, noteToggle);
                    await PostChordAsync(NotesShortcutController.ToggleId, lensChord);
                    Require(lensCount == 1 && toggleCount == 1 && focusCount == 1, "A mismatched queued native chord activated a tool");
                    Require(lens.Suspend(true) && notes.Suspend(true), "Native shortcuts did not suspend");
                    await PostChordAsync(0xB091, lensChord); await PostChordAsync(NotesShortcutController.FocusId, noteFocus);
                    Require(lensCount == 1 && focusCount == 1 && !lens.HasMessageHook && !notes.HasMessageHook,
                        "Suspending native shortcuts retained a hook or callback");
                    Require(lens.Suspend(false) && notes.Suspend(false), "Native shortcuts did not resume");
                    await PostChordAsync(0xB091, lensChord); await PostChordAsync(NotesShortcutController.ToggleId, noteToggle);
                    Require(lensCount == 2 && toggleCount == 2, "Resumed native shortcuts stopped receiving messages");
                }
            }
            source.Dispose();
            Require(source.Handle == IntPtr.Zero && !ReviewIsWindow(handle), "Native source disposal retained its HWND");
            source.Dispose();

            async Task PostChordAsync(int id, KeyboardShortcut chord)
            {
                Require(PostNativeFixtureMessage(handle, 0x0312, new IntPtr(id),
                    new IntPtr((long)chord.VirtualKey << 16 | chord.Modifiers)), "Could not post an owned native hotkey message");
                await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            }
        }
        var removed = new NativeFixtureWindowClass { Size = (uint)Marshal.SizeOf<NativeFixtureWindowClass>() };
        Require(!GetNativeFixtureClassInfo(module, className, ref removed), "Disposed native source retained its registered window class");
        using (var replacement = new NativeMessageWindow("DailyToolkit isolated replacement fixture"))
        using (var lens = new LensShortcutController(replacement, _ => false, hotkeyId: 0xB091))
        using (var notes = new NotesShortcutController(replacement))
        {
            Require(ReviewIsWindow(replacement.Handle) && NativeFixtureEnumerates(replacement.Handle), "Native source recreation failed");
            var received = 0;
            HwndSourceHook replacementHook = (IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (message == probe) { received++; handled = true; }
                return IntPtr.Zero;
            };
            replacement.AddHook(replacementHook);
            Require(PostNativeFixtureMessage(replacement.Handle, probe, IntPtr.Zero, IntPtr.Zero), "Could not post to a recreated native source");
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            Require(received == 1, "Recreated native source did not receive its posted user message");
            replacement.RemoveHook(replacementHook);
            if (!hotkeysUnavailable) Require(lens.Configure(new(6, 0x87, "Ctrl + Shift + F24"), LensActivationMode.Toggle) &&
                notes.Configure(new(3, 0x86, "Ctrl + Alt + F23"), new(7, 0x86, "Ctrl + Alt + Shift + F23")),
                "Prior native source disposal leaked shortcut registrations");
        }
        Console.WriteLine("PASS Pure native top-level HWND: hook order/self-removal/handled results, posted user message, HWND/class disposal and recreation; no physical input; " +
            (hotkeysUnavailable ? "native hotkey callbacks explicitly skipped for confirmed private-station denial" : "native lens/notes hotkey callbacks, suspension/resume and registration disposal verified"));
    }

    private static async Task CheckNativeRuntimeMessagesAsync()
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-runtime-native-messages-");
        MainViewModel? model = null; ApplicationRuntimeHost? runtime = null;
        try
        {
            model = await DisabledRuntimeFixtureModelAsync(fixture.FullName);
            runtime = new(model, enableRuntime: false, idleDelay: TimeSpan.FromMilliseconds(100), shutdownApplication: false);
            var creations = 0;
            runtime.WindowCreated += window => { ConfigureRuntimeFixtureWindow(window); creations++; };
            var source = runtime.SourceHandle;
            Require(runtime.Window is null && NativeFixtureEnumerates(source) && GetNativeFixtureParent(source) == IntPtr.Zero &&
                GetNativeFixtureProperty(source, SingleInstance.WindowMarker) != IntPtr.Zero,
                "Cold native runtime lacks its top-level single-instance marker or eagerly created UI");
            Require(PostNativeFixtureMessage(source, SingleInstance.ShowMessage, IntPtr.Zero, IntPtr.Zero), "Could not post cold runtime ShowMessage");
            await WaitNativeFixtureAsync(() => runtime.Window?.IsVisible == true, "Cold native ShowMessage did not create UI");
            await SettleRuntimeFixtureAsync();
            var first = runtime.Window!; var firstHandle = new WindowInteropHelper(first).Handle;
            model.Page = "floating-notes";
            await WaitNativeFixtureAsync(() => first.CreatedSettingsViewCount == 1, "Native runtime did not create its selected deferred settings page");
            await SettleRuntimeFixtureAsync();
            first.Hide();
            // Production idle policy releases the deferred pages while retaining
            // the main HWND; complete UI release is a separate explicit path.
            await WaitNativeFixtureAsync(() => first.CreatedSettingsViewCount == 0 && first.IsDeferredAutomationCleanupComplete,
                "Hidden runtime deferred page and accessibility cleanup did not finish after its idle timer");
            Require(ReferenceEquals(runtime.Window, first) && ReviewIsWindow(firstHandle) && !first.IsVisible &&
                runtime.SourceHandle == source && ReviewIsWindow(source),
                "Automatic deferred-page release replaced the main HWND or destroyed the native host");
            Require(runtime.TryReleaseHiddenWindow() && runtime.Window is null,
                "Explicit full UI release failed after the production deferred-page idle release");
            Require(!ReviewIsWindow(firstHandle) && runtime.SourceHandle == source && ReviewIsWindow(source),
                "Explicit full UI release destroyed the native host or retained the old UI HWND");
            Require(PostNativeFixtureMessage(source, SingleInstance.ShowMessage, IntPtr.Zero, IntPtr.Zero), "Could not post rebuilt runtime ShowMessage");
            await WaitNativeFixtureAsync(() => runtime.Window?.IsVisible == true, "Native ShowMessage did not rebuild released UI");
            Require(creations == 2 && !ReferenceEquals(first, runtime.Window) && ReferenceEquals(runtime.Window!.DataContext, model),
                "Native ShowMessage reused a closed window or replaced the shared model");
            model.Notes.FontSize = 29;
            model.ClipRecall.MaximumRecords = 4321;
            Require(PostNativeFixtureMessage(source, SingleInstance.ExitMessage, IntPtr.Zero, IntPtr.Zero), "Could not post native ExitMessage");
            await WaitNativeFixtureAsync(() => runtime.SourceHandle == IntPtr.Zero, "Native ExitMessage did not complete normal runtime exit", 15000);
            Require(runtime.Window is null && !ReviewIsWindow(source) &&
                (await model.Notes.Store.LoadAsync()).Preferences.Font.Size == 29 &&
                (await model.ClipRecall.Store.LoadAsync()).Settings.MaximumRecords == 4321,
                "Native ExitMessage retained windows or bypassed normal pending-settings persistence");
            Console.WriteLine("PASS Native runtime ShowMessage cold creation, timer-driven deferred-page release retaining the main HWND, explicit full UI release, ShowMessage reconstruction and ExitMessage persistence/disposal");
        }
        finally { await CloseRuntimeFixtureAsync(runtime, model, fixture); }
    }

    private static async Task WaitNativeFixtureAsync(Func<bool> predicate, string failure, int timeoutMilliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Require(predicate(), failure);
    }

    private static bool IsNativeFixtureStationDenial(int error) => error is 5 or 1459;

    private static bool NativeFixtureEnumerates(IntPtr expected)
    {
        var found = false;
        Require(EnumNativeFixtureWindows((window, _) => { if (window == expected) found = true; return true; }, IntPtr.Zero),
            "Could not enumerate the private-station fixture windows");
        return found;
    }

    private delegate bool NativeFixtureWindowCallback(IntPtr window, IntPtr data);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFixtureWindowClass
    {
        public uint Size, Style;
        public IntPtr Procedure;
        public int ClassExtra, WindowExtra;
        public IntPtr Instance, Icon, Cursor, Background, MenuName, ClassName, SmallIcon;
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendNativeFixtureMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostNativeFixtureMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetParent")]
    private static extern IntPtr GetNativeFixtureParent(IntPtr window);
    [DllImport("user32.dll", EntryPoint = "EnumWindows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumNativeFixtureWindows(NativeFixtureWindowCallback callback, IntPtr data);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    private static extern int GetNativeFixtureClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll", EntryPoint = "GetClassInfoExW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNativeFixtureClassInfo(IntPtr instance, string name, ref NativeFixtureWindowClass definition);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetNativeFixtureModule(string? name);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetNativeFixtureProperty(IntPtr window, string name);
}
