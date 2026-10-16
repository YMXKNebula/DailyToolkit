using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DailyToolkit.Core.Gaming;

namespace DailyToolkit.Desktop.ClipRecall;

public partial class ClipboardSettingsView : UserControl
{
    internal Func<IEnumerable<KeyboardShortcut?>>? ReservedShortcuts { get; set; }
    internal Action<bool>? ShortcutEditing { get; set; }
    private ClipboardHistoryViewModel? Model => DataContext as ClipboardHistoryViewModel;
    private ClipboardHistoryViewModel? _appearanceModel;
    public ClipboardSettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_appearanceModel is not null) _appearanceModel.PropertyChanged -= AppearanceChanged;
            _appearanceModel = Model;
            if (_appearanceModel is not null) _appearanceModel.PropertyChanged += AppearanceChanged;
            UpdatePalettePreview();
        };
        Loaded += async (_, _) => { if (Model is { } model) { model.PropertyChanged -= AppearanceChanged; model.PropertyChanged += AppearanceChanged; await model.RefreshStorageAsync(); } UpdatePalettePreview(); };
        Unloaded += (_, _) => { if (_appearanceModel is not null) _appearanceModel.PropertyChanged -= AppearanceChanged; };
    }
    private void AppearanceChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(ClipboardHistoryViewModel.AppearanceDraft) or "") UpdatePalettePreview(); }
    private void UpdatePalettePreview() => Model?.ApplyAppearance(PalettePreview.Resources, draft: true);
    private void OpenPanel(object sender, RoutedEventArgs e) { if (Model is { } model) { model.SavedOnly = false; model.OpenPanel(); } }
    private void OpenSaved(object sender, RoutedEventArgs e) { if (Model is { } model) { model.SavedOnly = true; model.OpenPanel(); } }
    private void OpenDataMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is not { } menu) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
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
        if (Model?.SetShortcut(new((uint)modifiers, (uint)KeyInterop.VirtualKeyFromKey(key), string.Join(" + ", labels)), ReservedShortcuts?.Invoke() ?? []) == true)
            Keyboard.ClearFocus();
    }
    private void ClearShortcut(object sender, RoutedEventArgs e) => Model?.SetShortcut(null, ReservedShortcuts?.Invoke() ?? []);
    private void ResetShortcut(object sender, RoutedEventArgs e) => Model?.SetShortcut(new(3, 0x56, "Ctrl + Alt + V"), ReservedShortcuts?.Invoke() ?? []);
    private async void SaveSettings(object sender, RoutedEventArgs e) { if (Model is { } model && await model.FlushSettingsAsync()) model.Notice = ""; }
    private async void ResetSettings(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && MessageBox.Show("恢复默认设置并关闭监听？历史记录会保留。", "恢复默认设置", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await model.ResetSettingsAsync();
    }
    private async void ClearOrdinary(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && MessageBox.Show("清空全部普通历史？永久保存记录会保留。此操作无法撤销，最近备份也会移除。", "清空普通历史", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            await model.ClearAsync(false);
    }
    private async void ClearAll(object sender, RoutedEventArgs e)
    {
        if (Model is { } model && MessageBox.Show("清空全部历史、永久保存项、缓存和备份？无法撤销，已复制的缓存路径也会失效。", "清空全部历史", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            await model.ClearAsync(true);
    }
    private async void Backup(object sender, RoutedEventArgs e) { if (Model is { } model) await model.BackupAsync(); }
    private async void RestoreBackup(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        if (model.IsEnabled && !model.IsPaused) { model.Notice = "恢复备份前请暂停监听。"; return; }
        if (MessageBox.Show("恢复最近备份？较新的记录不会合并，当前历史库另存到 recovery 目录。", "恢复最近备份", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            await model.RestoreBackupAsync();
    }
    private async void OpenDirectory(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model) return;
        try
        {
            await Task.Run(() => Directory.CreateDirectory(model.Store.DirectoryPath));
            Process.Start(new ProcessStartInfo(model.Store.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        { model.Notice = "数据目录未能打开，请检查权限。"; }
    }
}
