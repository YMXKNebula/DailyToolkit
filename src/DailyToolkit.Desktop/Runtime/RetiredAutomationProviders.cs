using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DailyToolkit.Desktop.Runtime;

// A page that will never be reused has the same UIA lifetime as a destroyed
// element. Disconnect only its providers; the retained main window and every
// independent tool keep their own accessibility providers.
internal sealed class RetiredAutomationProviders
{
    private const int ElementNotAvailable = unchecked((int)0x80040201);
    private const int ObjectNotConnected = unchecked((int)0x800401FD);
    private const int RpcDisconnected = unchecked((int)0x80010108);
    private const int RpcCannotCallOut = unchecked((int)0x8001010D);
    private static readonly Lazy<bool> ElementPeersUseWeakReferences = new(ReadElementPeersUseWeakReferences);
    private readonly Dispatcher _dispatcher;
    private readonly WeakReference<FrameworkElement> _view;
    private readonly WeakReference<AutomationPeer>? _survivingParent;
    private readonly List<IRawElementProviderSimple> _providers = [];
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _scheduled, _parentRefreshed, _recordedFailure;

    private RetiredAutomationProviders(FrameworkElement view, AutomationPeer? survivingParent)
    {
        _dispatcher = view.Dispatcher;
        _view = new(view);
        if (survivingParent is not null) _survivingParent = new(survivingParent);
    }

    internal static RetiredAutomationProviders Capture(FrameworkElement view)
    {
        ArgumentNullException.ThrowIfNull(view);
        view.Dispatcher.VerifyAccess();
        var rootPeer = UIElementAutomationPeer.FromElement(view);
        AutomationPeer? survivingParent = null;
        Exception? parentFailure = null;
        try { survivingParent = rootPeer?.GetParent(); }
        catch (Exception error) when (IsAutomationFailure(error)) { parentFailure = error; }
        var result = new RetiredAutomationProviders(view, survivingParent);
        if (parentFailure is not null) result.RecordFailure(parentFailure);
        var peers = new HashSet<AutomationPeer>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<AutomationPeer>();
        foreach (var element in Elements(view))
            if (element is UIElement ui && UIElementAutomationPeer.FromElement(ui) is { } existing)
                pending.Push(existing);
        while (pending.TryPop(out var peer))
        {
            if (!peers.Add(peer)) continue;
            try
            {
                // Only the initial seeds are existing peers. GetChildren can
                // materialize more children, including virtual item peers;
                // traversal stays within this permanently retired subtree.
                if (peer.GetChildren() is { } children)
                    foreach (var child in children.ToArray()) pending.Push(child);
            }
            catch (Exception error) when (IsAutomationFailure(error)) { result.RecordFailure(error); }
        }
        if (peers.Count == 0) return result;

        var bridge = new PeerProviderBridge(view);
        var providers = new HashSet<IRawElementProviderSimple>(ReferenceEqualityComparer.Instance);
        var filterElementPeers = ElementPeersUseWeakReferences.Value;
        foreach (var peer in peers)
        {
            // WPF's default ElementProxy keeps these element peers weakly. Once
            // the retired owner/peer is collected, their old client providers
            // naturally become unavailable. Avoid creating extra proxies just
            // to disconnect them; item and other non-element peers need explicit
            // disconnection because their proxies retain the peer strongly. The
            // compatibility key can disable weak proxies; then retain all peers.
            if (filterElementPeers && peer is (UIElementAutomationPeer or ContentElementAutomationPeer or UIElement3DAutomationPeer)) continue;
            try
            {
                // ProviderFromPeer validates connectivity and reuses an existing
                // ElementProxy. Capture while the hidden page is still attached;
                // doing this after detachment can return null instead.
                if (bridge.Wrap(peer) is { } provider && providers.Add(provider)) result._providers.Add(provider);
            }
            catch (Exception error) when (IsAutomationFailure(error)) { result.RecordFailure(error); }
        }
        return result;
    }

    internal Task<bool> DisconnectWhenIdle(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        dispatcher.VerifyAccess();
        if (!ReferenceEquals(dispatcher, _dispatcher)) throw new InvalidOperationException("The retired page belongs to another dispatcher.");
        if (_scheduled) return _completion.Task;
        _scheduled = true;
        if (_providers.Count == 0 && _survivingParent is null) Complete();
        else QueueDisconnect(retry: false);
        return _completion.Task;
    }

    private void QueueDisconnect(bool retry)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            RecordFailure(new InvalidOperationException("Accessibility cleanup could not run before dispatcher shutdown."));
            Complete();
            return;
        }
        var operation = _dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => Disconnect(retry)));
        operation.Aborted += CleanupAborted;
        if (operation.Status == DispatcherOperationStatus.Aborted) CleanupAborted(operation, EventArgs.Empty);
    }

    private void Disconnect(bool retry)
    {
        // UiaDisconnectProvider performs outbound COM work. It must never run
        // inside a synchronous SendMessage/WM_GETOBJECT response.
        if (InSendMessageEx(IntPtr.Zero) != 0)
        {
            if (!retry) { QueueDisconnect(retry: true); return; }
            RecordFailure(new COMException("Accessibility cleanup remained inside a synchronous message.", RpcCannotCallOut));
            Complete();
            return;
        }
        if (_view.TryGetTarget(out var view))
        {
            if (view.IsVisible || view.Parent is not null || VisualTreeHelper.GetParent(view) is not null)
            {
                RecordFailure(new InvalidOperationException("Accessibility providers cannot be disconnected from a retained page."));
                Complete();
                return;
            }
            if (view.IsLoaded)
            {
                if (!retry) { QueueDisconnect(retry: true); return; }
                RecordFailure(new InvalidOperationException("The retired page has not completed unloading."));
                Complete();
                return;
            }
        }
        if (!_parentRefreshed)
        {
            _parentRefreshed = true;
            try
            {
                // Refresh the surviving parent's child cache after removal so a
                // reentrant client cannot rediscover the retired child providers.
                // This does not disconnect the parent's own provider.
                if (_survivingParent is { } reference && reference.TryGetTarget(out var parent)) parent.ResetChildrenCache();
            }
            catch (Exception error) when (IsAutomationFailure(error)) { RecordFailure(error); }
        }

        var retryProviders = new List<IRawElementProviderSimple>();
        foreach (var provider in _providers)
        {
            try
            {
                var status = UiaDisconnectProvider(provider);
                if (status >= 0 || status is ElementNotAvailable or ObjectNotConnected or RpcDisconnected) continue;
                if (status == RpcCannotCallOut && !retry) retryProviders.Add(provider);
                else RecordFailure(new COMException("A retired accessibility provider could not be disconnected.", status));
            }
            catch (Exception error) when (IsAutomationFailure(error) || error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                if (error.HResult == RpcCannotCallOut && !retry) retryProviders.Add(provider);
                else RecordFailure(error);
            }
        }
        _providers.Clear();
        if (retryProviders.Count == 0) { Complete(); return; }
        _providers.AddRange(retryProviders);
        QueueDisconnect(retry: true);
    }

    private void CleanupAborted(object? sender, EventArgs args)
    {
        if (sender is DispatcherOperation operation) operation.Aborted -= CleanupAborted;
        RecordFailure(new InvalidOperationException("Accessibility cleanup was aborted before completion."));
        Complete();
    }

    private void Complete()
    {
        _providers.Clear();
        _completion.TrySetResult(!_recordedFailure);
    }

    private void RecordFailure(Exception error)
    {
        // The existing diagnostic service retains one fixed category, not UI
        // text, provider identity, content, paths or an unbounded error history.
        if (_recordedFailure) return;
        _recordedFailure = true;
        DiagnosticReportService.Shared.RecordError(DiagnosticModule.Application, error);
    }

    private static bool IsAutomationFailure(Exception error) =>
        error is ElementNotAvailableException or InvalidOperationException or COMException;

    private static bool ReadElementPeersUseWeakReferences()
    {
        // Match WPF's process-local registry view and exact RegistryKeys.WPF_Features
        // path. Read once, never write. Unknown types/failures use the conservative
        // full cleanup rather than assuming the default weak-proxy behavior.
        // https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/Shared/MS/Internal/Registry.cs
        try
        {
            var value = Registry.GetValue(@"HKEY_LOCAL_MACHINE\Software\Microsoft\.NETFramework\Windows Presentation Foundation\Features",
                "AutomationWeakReferenceDisallow", null);
            return value is null || value is int number && number == 0;
        }
        catch (Exception error) when (error is SecurityException or UnauthorizedAccessException or IOException)
        {
            DiagnosticReportService.Shared.RecordError(DiagnosticModule.Application, error);
            return false;
        }
    }

    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    {
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.TryPop(out var current))
        {
            if (!seen.Add(current)) continue;
            yield return current;
            foreach (var child in LogicalTreeHelper.GetChildren(current))
                if (child is DependencyObject dependency) pending.Push(dependency);
            if (current is not Visual && current is not Visual3D) continue;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                pending.Push(VisualTreeHelper.GetChild(current, index));
        }
    }

    private sealed class PeerProviderBridge(FrameworkElement owner) : FrameworkElementAutomationPeer(owner)
    {
        internal IRawElementProviderSimple? Wrap(AutomationPeer peer) => ProviderFromPeer(peer);
    }

    [DllImport("UIAutomationCore.dll", ExactSpelling = true)]
    private static extern int UiaDisconnectProvider([MarshalAs(UnmanagedType.Interface)] IRawElementProviderSimple provider);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint InSendMessageEx(IntPtr reserved);
}
