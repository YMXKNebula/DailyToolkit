using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Core.Notes;
using Microsoft.Win32;

namespace DailyToolkit.Desktop.Notes;

public partial class NotesSettingsView : UserControl
{
    private NotesViewModel? _model;
    internal Func<KeyboardShortcut?>? LensShortcut { get; set; }
    internal Action<bool>? ShortcutEditing { get; set; }
    internal Action? ToggleRequested { get; set; }
    public NotesSettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.PropertyChanged -= Changed;
            _model = DataContext as NotesViewModel;
            if (_model is not null) _model.PropertyChanged += Changed;
            if (IsVisible) _model?.LoadFontList();
            UpdatePreview();
        };
        IsVisibleChanged += (_, _) => { if (IsVisible) { _model?.LoadFontList(); UpdatePreview(); } };
        Unloaded += (_, _) => { if (_model is not null) _model.PropertyChanged -= Changed; };
        Loaded += (_, _) =>
        {
            if (_model is not null) { _model.PropertyChanged -= Changed; _model.PropertyChanged += Changed; }
            if (IsVisible) { _model?.LoadFontList(); UpdatePreview(); }
        };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (IsVisible && e.PropertyName == "") UpdatePreview(); }
    private void SettingsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        AppearanceCard.Height = Math.Max(160, e.NewSize.Height - 12);
    }
    private void AppearanceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var sideBySide = e.NewSize.Width >= 600;
        AppearanceLayout.ColumnDefinitions[0].Width = new(3, GridUnitType.Star);
        AppearanceLayout.ColumnDefinitions[1].Width = sideBySide ? new(2, GridUnitType.Star) : new(0);
        AppearanceLayout.RowDefinitions[0].Height = sideBySide ? new(1, GridUnitType.Star) : new(Math.Min(180, e.NewSize.Height * .36));
        AppearanceLayout.RowDefinitions[1].Height = sideBySide ? new(0) : new(1, GridUnitType.Star);
        Grid.SetRow(AppearanceScroll, sideBySide ? 0 : 1);
        Grid.SetColumn(PreviewPanel, sideBySide ? 1 : 0);
        PreviewPanel.Margin = sideBySide ? new(18, 0, 0, 0) : new(0, 0, 8, 12);
        UpdatePreviewDimensions();
    }
    private void UpdatePreviewDimensions()
    {
        if (_model is null) return;
        var sideBySide = AppearanceLayout.ActualWidth >= 600;
        var width = Math.Max(0, AppearanceLayout.ActualWidth * (sideBySide ? .4 : 1) - (sideBySide ? 18 : 8));
        var availableHeight = sideBySide ? AppearanceLayout.ActualHeight : AppearanceLayout.RowDefinitions[0].Height.Value;
        var height = width * _model.Height / _model.Width;
        AppearancePreview.Height = Math.Max(0, Math.Min(height, availableHeight - (sideBySide ? 34 : 46)));
    }
    private void UpdatePreview()
    {
        if (_model is null) return;
        var preferences = _model.Preferences;
        var focused = PreviewFocused.IsChecked == true;
        var opaque = focused && preferences.OpaqueWhenFocused;
        AppearancePreview.Background = NotesWindow.Brush(preferences.Background.Color, opaque ? 1 : preferences.Background.Opacity);
        AppearancePreview.CornerRadius = new(preferences.Background.Radius);
        AppearancePreview.BorderBrush = focused ? NotesWindow.Brush(preferences.FocusBorderColor, 1) : System.Windows.Media.Brushes.Transparent;
        BackgroundColorSwatch.Background = NotesWindow.Brush(preferences.Background.Color, 1);
        FontColorSwatch.Background = NotesWindow.Brush(preferences.Font.Color, 1);
        FocusColorSwatch.Background = NotesWindow.Brush(preferences.FocusBorderColor, 1);
        PreviewHide.Foreground = PreviewResize.Foreground = PreviewText.Foreground = NotesWindow.Brush(preferences.Font.Color, opaque ? 1 : preferences.Font.Opacity);
        PreviewResize.Visibility = preferences.AllowManualResize ? Visibility.Visible : Visibility.Collapsed;
        PreviewText.FontSize = preferences.Font.Size; PreviewText.FontFamily = new(preferences.Font.Family + ", Microsoft YaHei UI");
        PreviewText.FontWeight = preferences.Font.Bold ? FontWeights.Bold : FontWeights.Normal;
        UpdatePreviewDimensions();
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
        if (_model?.SetShortcut(((FrameworkElement)sender).Tag as string == "focus",
            new((uint)modifiers, (uint)KeyInterop.VirtualKeyFromKey(key), string.Join(" + ", labels)), LensShortcut?.Invoke()) == true)
            Keyboard.ClearFocus();
    }
    private void ClearShortcut(object sender, RoutedEventArgs e) => _model?.SetShortcut(((FrameworkElement)sender).Tag as string == "focus", null, LensShortcut?.Invoke());
    private void ResetShortcuts(object sender, RoutedEventArgs e) => _model?.ResetShortcuts(LensShortcut?.Invoke());
    private void ToggleNote(object sender, RoutedEventArgs e) => ToggleRequested?.Invoke();
    private void ResetPosition(object sender, RoutedEventArgs e) => _model?.ResetPosition();
    private void ResetBackground(object sender, RoutedEventArgs e) => _model?.ResetBackground();
    private void ResetFont(object sender, RoutedEventArgs e) => _model?.ResetFont();
    private void ResetSettings(object sender, RoutedEventArgs e) => _model?.ResetSettings();
    private void ApplyBackground(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).Tag is NotesBackground background) _model?.ApplyBackground(background); }
    private void ApplyFont(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).Tag is NotesFont font) _model?.ApplyFont(font); }
    private void PreviewFocusChanged(object sender, RoutedEventArgs e) { if (IsInitialized) UpdatePreview(); }
    private void ApplyAppearance(object sender, RoutedEventArgs e) { if (((FrameworkElement)sender).Tag is NotesColors colors) _model?.ApplyAppearance(colors); }
    private void ChooseColor(object sender, RoutedEventArgs e)
    {
        var picker = (((FrameworkElement)sender).Tag as string) switch { "font" => FontColorPicker, "focus" => FocusColorPicker, _ => BackgroundColorPicker };
        picker.OpenColorDialog();
    }
    private static bool Digits(string text) => text.Length > 0 && text.All(character => character is >= '0' and <= '9');
    private void SizeInput(object sender, TextCompositionEventArgs e) => e.Handled = !Digits(e.Text);
    private void PasteSize(object sender, DataObjectPastingEventArgs e)
    { if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText) || e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !Digits(text)) e.CancelCommand(); }
    private void CommitSize(object sender, KeyboardFocusChangedEventArgs e)
    {
        var box = (TextBox)sender;
        var binding = box.GetBindingExpression(TextBox.TextProperty);
        binding?.UpdateSource(); binding?.UpdateTarget();
    }
    private async void SaveNow(object sender, RoutedEventArgs e) { if (_model is not null && await _model.FlushAsync()) _model.Notice = "已保存。"; }
    private async void ExportText(object sender, RoutedEventArgs e)
    {
        if (_model is null || !_model.ContentWritable) return;
        var dialog = new SaveFileDialog { Title = "导出浮笺", Filter = "文本文件|*.txt", FileName = "浮笺.txt", DefaultExt = ".txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { await NotesStore.AtomicWriteAsync(dialog.FileName, _model.Text); _model.Notice = "笔记已导出。"; }
        catch (Exception exception) when (NotesStore.IsStorageError(exception)) { _model.Notice = "导出失败，请检查所选文件夹是否可写。"; }
    }
    private async void OpenDirectory(object sender, RoutedEventArgs e)
    {
        if (_model is null) return;
        try
        {
            var directory = _model.Store.DirectoryPath;
            await Task.Run(() => Directory.CreateDirectory(directory));
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception) when (NotesStore.IsStorageError(exception) || exception is Win32Exception)
        { _model.Notice = "未能打开浮笺数据目录，请检查目录权限。"; }
    }
}
