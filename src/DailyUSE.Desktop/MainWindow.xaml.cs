using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using DailyUSE.Core.Gaming;
using DailyUSE.Desktop.Gaming;
using System.Windows.Navigation;
using System.Windows.Threading;
using DailyUSE.Desktop.Presentation;
using Microsoft.Win32;

namespace DailyUSE.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closing;
    private readonly DispatcherTimer _clockTimer;
    private int _ticks;
    private LensShortcutController? _shortcuts;
    private const string HotkeyConflictNotice="放大快捷键可能被其他软件占用，请用游戏页的按钮控制。";

    public MainWindow(MainViewModel viewModel, bool enableShortcuts = true)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) =>
        {
            if (!enableShortcuts) return;
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (source is null) return;
            _shortcuts = new(source);
            _shortcuts.ToggleRequested += _viewModel.Gaming.Toggle;
            _shortcuts.ShowRequested += _viewModel.Gaming.Show;
            _shortcuts.HideRequested += _viewModel.Gaming.Hide;
            RegisterLensHotkey();
        };
        _viewModel.Gaming.PropertyChanged += OnGamingStateChanged;
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            _viewModel.UpdateClock();
            if (++_ticks % 3 == 0) _viewModel.QueueLocalRefresh();
        };
        Loaded += (_, _) => { _ = _viewModel.InitializeAsync(); _clockTimer.Start(); };
        SizeChanged += (_, _) => _viewModel.SetViewportWidth(ActualWidth);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            _shortcuts?.Dispose();
            _viewModel.Gaming.PropertyChanged -= OnGamingStateChanged;
            _viewModel.Dispose();
        };
    }

    internal FrameworkElement PreviewContent => RootContent;
    internal Task WaitForLensPreviewAsync() => LensPhotoPreview.IsVisible ? LensPhotoPreview.Ready : Task.CompletedTask;

    private void CommitLensSize(object sender,RoutedEventArgs e)
    {
        var binding=((System.Windows.Controls.TextBox)sender).GetBindingExpression(System.Windows.Controls.TextBox.TextProperty);
        binding?.UpdateSource();
        binding?.UpdateTarget();
    }

    private void LensSizeKeyDown(object sender,KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitLensSize(sender,e); e.Handled=true;
    }

    private void LensSettingsSizeChanged(object sender,SizeChangedEventArgs e)
    {
        var sideBySide=e.NewSize.Width >= 600;
        LensSettingsGrid.ColumnDefinitions[0].Width=sideBySide ? new GridLength(260) : new GridLength(1,GridUnitType.Star);
        LensSettingsGrid.ColumnDefinitions[1].Width=sideBySide ? new GridLength(1,GridUnitType.Star) : new GridLength(0);
        System.Windows.Controls.Grid.SetColumn(LensPhotoPreview,sideBySide ? 1 : 0);
        System.Windows.Controls.Grid.SetRow(LensPhotoPreview,sideBySide ? 0 : 1);
        LensPhotoPreview.Margin=sideBySide ? new Thickness(18,0,0,18) : new Thickness(0,8,0,18);
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        _shortcuts?.Suspend(true);
        _viewModel.Gaming.Stop();
        if (_closing) { e.Cancel = true; return; }
        var pending = _viewModel.PendingWork;
        if (pending.IsCompleted) return;
        e.Cancel = true;
        _closing = true;
        _clockTimer.Stop();
        _viewModel.CancelPending();
        await pending;
        _closing = false;
        Close();
    }

    private void OnGamingStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(GamingViewModel.ToggleShortcut) or nameof(GamingViewModel.ActivationMode))) return;
        RegisterLensHotkey();
    }
    private void RegisterLensHotkey()
    {
        if (_shortcuts is null) return;
        UpdateShortcutNotice(_shortcuts.Configure(_viewModel.Gaming.ToggleShortcut,_viewModel.Gaming.ActivationMode));
    }
    private void UpdateShortcutNotice(bool registered)
    {
        if (!registered) _viewModel.Notice = HotkeyConflictNotice;
        else if (_viewModel.Notice == HotkeyConflictNotice) _viewModel.Notice = "";
    }
    private void ShortcutFocus(object sender,KeyboardFocusChangedEventArgs e)
    {
        _shortcuts?.Suspend(true);
    }
    private void ShortcutBlur(object sender,KeyboardFocusChangedEventArgs e)
    {
        if (_shortcuts is not null) UpdateShortcutNotice(_shortcuts.Suspend(false));
    }
    private void CaptureShortcut(object sender,KeyEventArgs e)
    {
        var key=e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return;
        e.Handled=true;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { Keyboard.ClearFocus(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        var modifiers = Keyboard.Modifiers;
        var labels=new List<string>();
        if(modifiers.HasFlag(ModifierKeys.Control)) labels.Add("Ctrl");
        if(modifiers.HasFlag(ModifierKeys.Alt)) labels.Add("Alt");
        if(modifiers.HasFlag(ModifierKeys.Shift)) labels.Add("Shift");
        if(modifiers.HasFlag(ModifierKeys.Windows)) labels.Add("Win");
        labels.Add(key is >= Key.D0 and <= Key.D9 ? ((int)key-(int)Key.D0).ToString() : key.ToString());
        var binding=new KeyboardShortcut((uint)modifiers,(uint)KeyInterop.VirtualKeyFromKey(key),string.Join(" + ",labels));
        if (!binding.IsValid) return;
        _viewModel.Gaming.SetShortcut(binding);
        Keyboard.ClearFocus();
    }
    private void ClearShortcut(object sender,RoutedEventArgs e) =>
        _viewModel.Gaming.SetShortcut(null);

    private void ToggleLensDetails(object sender,RoutedEventArgs e) =>
        SetLensDetails(LensDetailsPanel.Visibility != Visibility.Visible);

    private void SetLensDetails(bool expanded)
    {
        LensDetailsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        LensDetailsToggle.Content = expanded ? "▴" : "▾";
    }

    private async void SaveReport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存电脑信息", FileName = $"DailyUSE-report-{DateTime.Now:yyyyMMdd-HHmm}.json",
            Filter = "JSON 报告 (*.json)|*.json", DefaultExt = ".json", AddExtension = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, _viewModel.ExportJson(), new UTF8Encoding(false));
            _viewModel.Notice = "报告已保存。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _viewModel.Notice = "没有保存成功，请换一个位置再试。";
        }
    }

    internal void ShowPreviewDetails(bool software)
    {
        if (_viewModel.ShowScreenLens) { SetLensDetails(true); UpdateLayout(); return; }
        HardwareExpander.IsExpanded = true;
        SoftwareExpander.IsExpanded = software;
        UpdateLayout();
        HomeScroll.ScrollToVerticalOffset(software ? SoftwareExpander.TranslatePoint(new(0, 0), HomeScroll).Y : 320);
    }

    private void CopySummary(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"{_viewModel.SystemName} {_viewModel.SystemDetail}\n" +
                $"处理器：{_viewModel.CpuName}（{_viewModel.CpuDetail}）\n" +
                $"内存：{_viewModel.MemoryTotal}\n显卡：{_viewModel.GraphicsText}\n" +
                $"架构：{_viewModel.Architecture}\n显示：{_viewModel.DisplayDetail}");
            _viewModel.Notice = "摘要已复制。";
        }
        catch (ExternalException) { _viewModel.Notice = "剪贴板正忙，请稍后再试。"; }
    }

    private void OpenRepository(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception) { _viewModel.Notice = "没有打开浏览器。"; }
        e.Handled = true;
    }
}
