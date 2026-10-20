using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.FilePack;

public partial class FilePackSettingsView : UserControl
{
    private FilePackViewModel? _model;
    internal Func<IEnumerable<KeyboardShortcut?>>? ReservedShortcuts { get; set; }
    internal Action<bool>? ShortcutEditing { get; set; }
    internal FilePackContextMenuRegistration ContextMenuRegistration { get; set; } = new();
    public FilePackSettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { _model?.SetSettingsVisible(false); Detach(); _model = DataContext as FilePackViewModel; Attach(); UpdateAppearance(); UpdateShortcutWindow(); UpdateVisibility(); };
        Loaded += (_, _) => { Attach(); UpdateAppearance(); UpdateShortcutWindow(); RefreshContextMenu(); UpdateVisibility(); };
        IsVisibleChanged += (_, _) => UpdateVisibility();
        Unloaded += (_, _) => { Detach(); _model?.SetSettingsVisible(false); ShortcutEditing?.Invoke(false); };
    }
    private void UpdateVisibility() => _model?.SetSettingsVisible(IsLoaded && IsVisible);
    private void Attach()
    {
        if (_model is null) return;
        Detach(); _model.PropertyChanged += Changed;
        SystemParameters.StaticPropertyChanged += Changed;
        foreach (var appearance in _model.Appearances) appearance.PropertyChanged += Changed;
    }
    private void Detach()
    {
        if (_model is null) return;
        _model.PropertyChanged -= Changed;
        SystemParameters.StaticPropertyChanged -= Changed;
        foreach (var appearance in _model.Appearances) appearance.PropertyChanged -= Changed;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or nameof(SystemParameters.HighContrast) or nameof(FilePackViewModel.SelectedAppearance) or nameof(FilePackAppearance.Applied) or nameof(FilePackAppearance.Draft)) UpdateAppearance();
        if (e.PropertyName is "" or nameof(FilePackViewModel.ShortcutWindow) or nameof(FilePackViewModel.SettingsLoaded)) UpdateShortcutWindow();
    }
    private void UpdateShortcutWindow()
    {
        if (_model is null) return;
        QuickShortcutChoice.IsChecked = _model.ShortcutWindow == FilePackShortcutWindow.Quick;
        DetailedShortcutChoice.IsChecked = _model.ShortcutWindow == FilePackShortcutWindow.Detailed;
    }
    private void SelectShortcutWindow(object sender, RoutedEventArgs e)
    {
        if (_model?.SettingsLoaded == true && sender is RadioButton { IsChecked: true, Tag: string value } &&
            Enum.TryParse<FilePackShortcutWindow>(value, out var choice)) _model.ShortcutWindow = choice;
    }
    private void UpdateAppearance()
    { _model?.SelectedAppearance.Apply(PalettePreview.Resources, draft: true); }
    private void ShortcutFocus(object sender, KeyboardFocusChangedEventArgs e) => ShortcutEditing?.Invoke(true);
    private void ShortcutBlur(object sender, KeyboardFocusChangedEventArgs e) => ShortcutEditing?.Invoke(false);
    private void CaptureShortcut(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return;
        e.Handled = true;
        if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers; var labels = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) labels.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) labels.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) labels.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) labels.Add("Win");
        labels.Add(key is >= Key.D0 and <= Key.D9 ? ((int)key - (int)Key.D0).ToString() : key.ToString());
        var shortcut = new KeyboardShortcut((uint)modifiers, (uint)KeyInterop.VirtualKeyFromKey(key), string.Join(" + ", labels));
        var accepted = _model?.SetShortcut(shortcut, ReservedShortcuts?.Invoke() ?? []) == true;
        (Window.GetWindow(this)?.DataContext as MainViewModel)?.Shortcuts.ReportAssignmentAttempt(ShortcutIds.FilePackToggle, shortcut, accepted);
        if (accepted) Keyboard.ClearFocus();
    }
    private void ClearShortcut(object sender, RoutedEventArgs e) => _model?.SetShortcut(null, ReservedShortcuts?.Invoke() ?? []);
    private void RefreshContextMenu()
    {
        try { ContextMenuCheck.IsChecked = ContextMenuRegistration.IsRegistered; }
        catch (Exception exception) when (FilePackViewModel.IsExpected(exception)) { _model?.SetNotice("右键菜单状态未能读取。"); }
    }
    private void ChangeContextMenu(object sender, RoutedEventArgs e)
    {
        try { ContextMenuRegistration.SetEnabled(ContextMenuCheck.IsChecked == true); }
        catch (Exception exception) when (FilePackViewModel.IsExpected(exception)) { _model?.SetNotice("右键菜单未能更改，请检查权限或软件位置。"); }
        RefreshContextMenu();
    }
}
