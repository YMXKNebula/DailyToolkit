using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using DailyToolkit.Desktop.Controls;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop;

// Only values survive an idle release: no control, binding, model or callback may
// be kept here. Deferred pages keep their values without creating their controls.
internal sealed record MainWindowUiState(Rect NormalBounds, WindowState LastVisibleState,
    UiScopeState[] Scopes, LensPreviewPosition PreviewPosition);
internal sealed record UiScopeState(string Id, UiElementState[] Elements);
internal readonly record struct UiScrollState(double Horizontal, double Vertical);
internal readonly record struct UiToggleState(bool? Value);
internal readonly record struct UiTextState(int SelectionStart, int SelectionLength, UiScrollState Scroll);
internal readonly record struct UiGridColumnState(int Index, double Width, DataGridLengthUnitType WidthUnit,
    int DisplayIndex, ListSortDirection? SortDirection);
internal sealed record UiElementState(string Path, UiScrollState? Scroll = null, bool? Expanded = null,
    UiToggleState? Toggle = null, int? SelectedIndex = null, UiTextState? Text = null, UiGridColumnState[]? Columns = null)
{
    internal bool HasValues => Scroll.HasValue || Expanded.HasValue || Toggle.HasValue ||
        SelectedIndex.HasValue || Text.HasValue || Columns is not null;
}

public partial class MainWindow
{
    private readonly Dictionary<string, Dictionary<string, UiElementState>> _pendingUiScopes = new(StringComparer.Ordinal);
    private DispatcherOperation? _uiRestoreOperation;
    private bool _uiStateHooks, _restoringUiState;

    internal bool IsUiBusy => UiReleaseBlockers() != UiReleaseBlocker.None;
    internal bool CanUnloadUi => !_closing && !_exitRequested && !_uiClosed &&
        (!IsVisible || WindowState == WindowState.Minimized) && !IsUiBusy;
    internal bool CanReclaimHiddenUiMemory => !IsVisible && CanUnloadUi && !IsLocalPolling &&
        IsDeferredAutomationCleanupComplete && LensPhotoPreview.IsIdleResourceCleanupComplete;
    internal bool IdleReleaseCanRetry
    {
        get
        {
            if (_closing || _exitRequested || _uiClosed || (IsVisible && WindowState != WindowState.Minimized)) return false;
            var blockers = UiReleaseBlockers();
            return (blockers & UiReleaseBlocker.Transient) != 0 && (blockers & UiReleaseBlocker.Draft) == 0;
        }
    }

    [Flags]
    private enum UiReleaseBlocker { None = 0, Transient = 1, Draft = 2 }

    private UiReleaseBlocker UiReleaseBlockers()
    {
        var result = UiReleaseBlocker.None;
        var handle = new WindowInteropHelper(this).Handle;
        if (_exportingDiagnostics || !_viewModel.CanEditStartup || ComponentDispatcher.IsThreadModal ||
            OwnedWindows.Count > 0 || (handle != IntPtr.Zero && !IsWindowEnabledForUiRelease(handle)) ||
            Mouse.Captured is not null || _restoringUiState || !_deferredAutomationCleanup.IsCompleted ||
            _uiRestoreOperation?.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing)
            result |= UiReleaseBlocker.Transient;
        foreach (var (_, element) in UiElements(RootContent))
        {
            if (element is FrameworkElement { DataContext: AppUpdateViewModel updates })
            {
                if (updates.IsBusy) result |= UiReleaseBlocker.Transient;
                if (updates.HasDownload) result |= UiReleaseBlocker.Draft;
            }
            if (element is ComboBox { IsDropDownOpen: true } || element is ContextMenu { IsOpen: true } ||
                element is FrameworkElement { ContextMenu.IsOpen: true }) result |= UiReleaseBlocker.Transient;
            if (element is not TextBox text || text.IsReadOnly) continue;
            // Do not commit LostFocus bindings or throw away an editing session just
            // to unload the interface. A later real visit lets the user resolve it.
            if (text.CanUndo || text.CanRedo || Validation.GetHasError(text) ||
                BindingOperations.GetBindingExpressionBase(text, TextBox.TextProperty)?.IsDirty == true)
                result |= UiReleaseBlocker.Draft;
            // Hex input intentionally uses OneWay: its uncommitted text is not a
            // dirty source binding and must be protected separately.
            if (text.GetBindingExpression(TextBox.TextProperty)?.DataItem is ColorPicker picker &&
                !string.Equals(text.Text, picker.Color, StringComparison.Ordinal)) result |= UiReleaseBlocker.Draft;
        }
        return result;
    }

    internal MainWindowUiState CaptureUiState()
    {
        Dispatcher.VerifyAccess();
        var scopes = CaptureUiScopes();
        var bounds = RestoreBounds;
        if (!UsableUiBounds(bounds)) bounds = new Rect(
            double.IsFinite(Left) ? Left : 100, double.IsFinite(Top) ? Top : 100,
            double.IsFinite(Width) && Width > 0 ? Width : 1100,
            double.IsFinite(Height) && Height > 0 ? Height : 760);
        return new(bounds, _lastVisibleState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal,
            scopes.Select(pair => new UiScopeState(pair.Key, pair.Value.Values.ToArray())).ToArray(),
            LensPhotoPreview.CapturePointerPosition());
    }

    private Dictionary<string, Dictionary<string, UiElementState>> CaptureUiScopes(bool includeMain = true)
    {
        var scopes = _pendingUiScopes.Where(pair => includeMain || pair.Key != "main").ToDictionary(pair => pair.Key,
            pair => new Dictionary<string, UiElementState>(pair.Value, StringComparer.Ordinal), StringComparer.Ordinal);
        foreach (var (id, root) in UiRoots())
        {
            if (!includeMain && id == "main") continue;
            var current = new Dictionary<string, UiElementState>(StringComparer.Ordinal);
            foreach (var (path, element) in UiElements(root, id == "main"))
            {
                var value = CaptureUiElement(path, element);
                if (value.HasValues) current[path] = value;
            }
            // Defaults in a page not yet visited must not overwrite values still
            // waiting for that page's first layout, including across two releases.
            if (scopes.TryGetValue(id, out var pending))
                foreach (var (path, value) in pending)
                    current[path] = current.TryGetValue(path, out var captured) ? MergeUiElement(captured, value) : value;
            scopes[id] = current;
        }
        return scopes;
    }

    internal void RestoreUiState(MainWindowUiState state)
    {
        Dispatcher.VerifyAccess();
        _pendingUiScopes.Clear();
        foreach (var scope in state.Scopes)
            _pendingUiScopes[scope.Id] = scope.Elements.ToDictionary(element => element.Path, StringComparer.Ordinal);
        if (UsableUiBounds(state.NormalBounds))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = state.NormalBounds.Left; Top = state.NormalBounds.Top;
            Width = Math.Max(MinWidth, state.NormalBounds.Width);
            Height = Math.Max(MinHeight, state.NormalBounds.Height);
        }
        _lastVisibleState = state.LastVisibleState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        WindowState = _lastVisibleState;
        LensPhotoPreview.RestorePointerPosition(state.PreviewPosition);
        AttachUiStateHooks();
        QueueUiStateRestore();
    }

    private static bool UsableUiBounds(Rect bounds) => !bounds.IsEmpty && double.IsFinite(bounds.X) &&
        double.IsFinite(bounds.Y) && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
        bounds.Width > 0 && bounds.Height > 0;

    internal void QueueUiStateRestore()
    {
        if (_pendingUiScopes.Count == 0 || _uiClosed || _closing || !IsLoaded || !IsVisible ||
            WindowState == WindowState.Minimized || _restoringUiState ||
            _uiRestoreOperation?.Status is DispatcherOperationStatus.Pending or DispatcherOperationStatus.Executing) return;
        _uiRestoreOperation = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _uiRestoreOperation = null;
            if (_uiClosed || !IsVisible || WindowState == WindowState.Minimized) return;
            _restoringUiState = true;
            try
            {
                ApplyUiStateProperties();
                UpdateLayout();
            }
            finally { _restoringUiState = false; }
            // Text selection/expansion can enqueue caret/layout work. Apply offsets
            // afterwards and only from still-pending state; real input clears it.
            _uiRestoreOperation = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                _uiRestoreOperation = null;
                if (_uiClosed || !IsVisible || WindowState == WindowState.Minimized) return;
                ApplyUiStateScrolls();
            }));
        }));
    }

    private void ApplyUiStateProperties()
    {
        foreach (var (id, root) in UiRoots())
        {
            if (!_pendingUiScopes.TryGetValue(id, out var pending)) continue;
            foreach (var (path, element) in UiElements(root, id == "main"))
            {
                if (element is not FrameworkElement { IsVisible: true, IsLoaded: true } ||
                    !pending.TryGetValue(path, out var state)) continue;
                if (state.Expanded is { } expanded && element is Expander expander)
                { expander.SetCurrentValue(Expander.IsExpandedProperty, expanded); state = state with { Expanded = null }; }
                if (state.Toggle is { } toggle && element is ToggleButton button)
                { button.SetCurrentValue(ToggleButton.IsCheckedProperty, toggle.Value); state = state with { Toggle = null }; }
                if (state.SelectedIndex is { } index && element is Selector selector)
                { selector.SetCurrentValue(Selector.SelectedIndexProperty, Math.Clamp(index, -1, selector.Items.Count - 1)); state = state with { SelectedIndex = null }; }
                if (state.Text is { } textState && element is TextBox text)
                {
                    var start = Math.Clamp(textState.SelectionStart, 0, text.Text.Length);
                    text.Select(start, Math.Clamp(textState.SelectionLength, 0, text.Text.Length - start));
                }
                if (state.Columns is { } columns && element is DataGrid grid)
                {
                    var available = columns.Where(column => column.Index >= 0 && column.Index < grid.Columns.Count).ToArray();
                    foreach (var column in available.OrderBy(column => column.DisplayIndex))
                    {
                        var target = grid.Columns[column.Index];
                        target.DisplayIndex = Math.Clamp(column.DisplayIndex, 0, grid.Columns.Count - 1);
                        // The shared collection view already owns its sort. Only
                        // restore the column header's indicator, never sort again.
                        target.SortDirection = column.SortDirection;
                    }
                    foreach (var column in available.Where(column => column.WidthUnit != DataGridLengthUnitType.Star))
                        RestoreUiGridWidth(grid.Columns[column.Index], column);
                    // Resizing or initially measuring a non-star column can
                    // redistribute existing star weights. Finish that layout
                    // before restoring the saved weights themselves.
                    grid.UpdateLayout();
                    foreach (var column in available.Where(column => column.WidthUnit == DataGridLengthUnitType.Star))
                        RestoreUiGridWidth(grid.Columns[column.Index], column);
                    state = state with { Columns = null };
                }
                if (state.HasValues) pending[path] = state; else pending.Remove(path);
            }
        }
        RemoveEmptyUiScopes();
    }

    private static void RestoreUiGridWidth(DataGridColumn target, UiGridColumnState column)
    {
        var width = new DataGridLength(column.Width, column.WidthUnit);
        // User-resized widths replace the XAML local value. Auto sizing
        // immediately coerces/transfers this property; a temporary current
        // value can revert to the old width.
        if (BindingOperations.IsDataBound(target, DataGridColumn.WidthProperty))
            target.SetCurrentValue(DataGridColumn.WidthProperty, width);
        else target.Width = width;
    }

    private void ApplyUiStateScrolls()
    {
        foreach (var (id, root) in UiRoots())
        {
            if (!_pendingUiScopes.TryGetValue(id, out var pending)) continue;
            foreach (var (path, element) in UiElements(root, id == "main"))
            {
                if (element is not FrameworkElement { IsVisible: true, IsLoaded: true } ||
                    !pending.TryGetValue(path, out var state)) continue;
                if (state.Scroll is { } scroll && element is ScrollViewer viewer)
                { viewer.ScrollToHorizontalOffset(scroll.Horizontal); viewer.ScrollToVerticalOffset(scroll.Vertical); state = state with { Scroll = null }; }
                if (state.Text is { } textState && element is TextBox text)
                { text.ScrollToHorizontalOffset(textState.Scroll.Horizontal); text.ScrollToVerticalOffset(textState.Scroll.Vertical); state = state with { Text = null }; }
                if (state.HasValues) pending[path] = state; else pending.Remove(path);
            }
        }
        RemoveEmptyUiScopes();
    }

    private static UiElementState CaptureUiElement(string path, DependencyObject element)
    {
        var value = new UiElementState(path);
        if (element is ScrollViewer { TemplatedParent: null } viewer)
            value = value with { Scroll = new(viewer.HorizontalOffset, viewer.VerticalOffset) };
        if (element is Expander expander && !BindingOperations.IsDataBound(expander, Expander.IsExpandedProperty))
            value = value with { Expanded = expander.IsExpanded };
        // This checkbox reflects external registration, rather than transient UI.
        // Its Loaded handler reads current state; restoring it could invoke an action.
        if (element is ToggleButton { TemplatedParent: null } toggle && toggle.Name != "ContextMenuCheck" &&
            !BindingOperations.IsDataBound(toggle, ToggleButton.IsCheckedProperty))
            value = value with { Toggle = new(toggle.IsChecked) };
        if (element is Selector selector && !BindingOperations.IsDataBound(selector, Selector.SelectedIndexProperty) &&
            !BindingOperations.IsDataBound(selector, Selector.SelectedItemProperty) && !BindingOperations.IsDataBound(selector, Selector.SelectedValueProperty))
            value = value with { SelectedIndex = selector.SelectedIndex };
        if (element is TextBox text)
            value = value with { Text = new(text.SelectionStart, text.SelectionLength, new(text.HorizontalOffset, text.VerticalOffset)) };
        if (element is DataGrid grid)
            value = value with { Columns = grid.Columns.Select((column, index) => new UiGridColumnState(index,
                column.Width.Value, column.Width.UnitType, column.DisplayIndex, column.SortDirection)).ToArray() };
        return value;
    }

    private static UiElementState MergeUiElement(UiElementState current, UiElementState pending) => current with
    {
        Scroll = pending.Scroll ?? current.Scroll, Expanded = pending.Expanded ?? current.Expanded,
        Toggle = pending.Toggle ?? current.Toggle, SelectedIndex = pending.SelectedIndex ?? current.SelectedIndex,
        Text = pending.Text ?? current.Text, Columns = pending.Columns ?? current.Columns
    };

    private IEnumerable<(string Id, FrameworkElement Root)> UiRoots()
    {
        yield return ("main", RootContent);
        if (_notesSettingsView is not null) yield return ("floating-notes", _notesSettingsView);
        if (_clipRecallSettingsView is not null) yield return ("clip-recall", _clipRecallSettingsView);
        if (_filePackSettingsView is not null) yield return ("file-pack", _filePackSettingsView);
    }

    private IEnumerable<(string Path, DependencyObject Element)> UiElements(DependencyObject root, bool skipDeferred = false)
    {
        var seen = new HashSet<DependencyObject>(ReferenceEqualityComparer.Instance);
        return Walk(root, "root");
        IEnumerable<(string, DependencyObject)> Walk(DependencyObject current, string path)
        {
            if (!seen.Add(current) || (skipDeferred && (ReferenceEquals(current, _notesSettingsView) ||
                ReferenceEquals(current, _clipRecallSettingsView) || ReferenceEquals(current, _filePackSettingsView)))) yield break;
            yield return (path, current);
            var logicalIndex = 0;
            foreach (var child in LogicalTreeHelper.GetChildren(current))
            {
                if (child is DependencyObject dependency)
                    foreach (var value in Walk(dependency, UiChildPath(path, "l", logicalIndex, dependency))) yield return value;
                logicalIndex++;
            }
            if (current is not Visual && current is not Visual3D) yield break;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
            {
                var child = VisualTreeHelper.GetChild(current, index);
                foreach (var value in Walk(child, UiChildPath(path, "v", index, child))) yield return value;
            }
        }
    }

    private static string UiChildPath(string parent, string kind, int index, DependencyObject child) =>
        $"{parent}/{kind}{index}:{child.GetType().Name}:{(child as FrameworkElement)?.Name}";

    private void AttachUiStateHooks()
    {
        if (_uiStateHooks) return;
        _uiStateHooks = true;
        Loaded += UiStateLoaded;
        IsVisibleChanged += UiStateVisibilityChanged;
        StateChanged += UiStateWindowStateChanged;
        Closed += UiStateClosed;
        _viewModel.PropertyChanged += UiStatePageChanged;
        AddHandler(Expander.ExpandedEvent, new RoutedEventHandler(UiStateExpanded));
        AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(UiStateInput), true);
        AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(UiStateInput), true);
        AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(UiStateInput), true);
        AddHandler(TextCompositionManager.PreviewTextInputEvent, new TextCompositionEventHandler(UiStateInput), true);
        AddHandler(UIElement.PreviewTouchDownEvent, new EventHandler<TouchEventArgs>(UiStateInput), true);
    }

    private void UiStateLoaded(object sender, RoutedEventArgs e) => QueueUiStateRestore();
    private void UiStateVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueUiStateRestore();
    private void UiStateWindowStateChanged(object? sender, EventArgs e) => QueueUiStateRestore();
    private void UiStateExpanded(object sender, RoutedEventArgs e) { if (!_restoringUiState) QueueUiStateRestore(); }
    private void UiStatePageChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(MainViewModel.Page)) QueueUiStateRestore(); }

    private void UiStateInput(object? sender, InputEventArgs e)
    {
        if (_restoringUiState) return;
        foreach (var (id, root) in UiRoots())
        {
            if (!_pendingUiScopes.TryGetValue(id, out var pending)) continue;
            foreach (var (path, element) in UiElements(root, id == "main"))
                if (element is FrameworkElement { IsVisible: true }) pending.Remove(path);
        }
        RemoveEmptyUiScopes();
    }

    private void RemoveEmptyUiScopes()
    {
        foreach (var id in _pendingUiScopes.Where(pair => pair.Value.Count == 0).Select(pair => pair.Key).ToArray())
            _pendingUiScopes.Remove(id);
    }

    private void UiStateClosed(object? sender, EventArgs e)
    {
        _uiRestoreOperation?.Abort(); _uiRestoreOperation = null;
        _viewModel.PropertyChanged -= UiStatePageChanged;
        // Other event handlers are owned by this window and contain no external
        // subscriptions. No view or delegate is copied into MainWindowUiState.
        _pendingUiScopes.Clear();
    }

    [DllImport("user32.dll", EntryPoint = "IsWindowEnabled")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowEnabledForUiRelease(IntPtr window);
}
