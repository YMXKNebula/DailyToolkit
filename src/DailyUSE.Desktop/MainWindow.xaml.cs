using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using DailyUSE.Core.Gaming;
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
    private HwndSource? _hotkeySource;
    private const int ToggleLensHotkey = 0xB011, CloseLensHotkey = 0xB012;
    private bool _editingShortcut;
    private const string HotkeyConflictNotice="放大快捷键可能被其他软件占用，请用游戏页的按钮控制。";

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += (_, _) =>
        {
            _hotkeySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _hotkeySource?.AddHook(HotkeyMessage);
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
            RemoveLensHotkeys();
            _hotkeySource?.RemoveHook(HotkeyMessage);
            _viewModel.Gaming.PropertyChanged -= OnGamingStateChanged;
            _viewModel.Dispose();
        };
    }

    internal FrameworkElement PreviewContent => RootContent;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
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
        if (e.PropertyName is not (nameof(GamingViewModel.IsActive) or nameof(GamingViewModel.ToggleShortcut) or nameof(GamingViewModel.CloseShortcut))) return;
        RegisterLensHotkeys();
    }
    private void RegisterLensHotkeys()
    {
        if (_hotkeySource is null) return;
        RemoveLensHotkeys();
        if (!_viewModel.Gaming.IsActive || _editingShortcut) return;
        var toggleBinding = _viewModel.Gaming.ToggleShortcut;
        var closeBinding = _viewModel.Gaming.CloseShortcut;
        var toggle = toggleBinding is null || RegisterHotKey(_hotkeySource.Handle,ToggleLensHotkey,toggleBinding.Modifiers | 0x4000,toggleBinding.VirtualKey);
        var close = closeBinding is null || RegisterHotKey(_hotkeySource.Handle,CloseLensHotkey,closeBinding.Modifiers | 0x4000,closeBinding.VirtualKey);
        if (!toggle || !close) _viewModel.Notice = HotkeyConflictNotice;
        else if (_viewModel.Notice == HotkeyConflictNotice) _viewModel.Notice="";
    }
    private void ShortcutFocus(object sender,KeyboardFocusChangedEventArgs e)
    {
        _editingShortcut=true;
        RemoveLensHotkeys();
    }
    private void ShortcutBlur(object sender,KeyboardFocusChangedEventArgs e)
    {
        _editingShortcut=false;
        RegisterLensHotkeys();
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
        _viewModel.Gaming.SetShortcut((sender as FrameworkElement)?.Tag as string == "toggle",binding);
        Keyboard.ClearFocus();
    }
    private void ClearShortcut(object sender,RoutedEventArgs e) =>
        _viewModel.Gaming.SetShortcut((sender as FrameworkElement)?.Tag as string == "toggle",null);
    private void RemoveLensHotkeys()
    {
        if (_hotkeySource is null) return;
        UnregisterHotKey(_hotkeySource.Handle,ToggleLensHotkey);
        UnregisterHotKey(_hotkeySource.Handle,CloseLensHotkey);
    }
    private IntPtr HotkeyMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam,ref bool handled)
    {
        if (message != 0x0312) return IntPtr.Zero;
        if (_editingShortcut) { handled=true; return IntPtr.Zero; }
        if (wParam.ToInt32() == ToggleLensHotkey && MatchesShortcut(_viewModel.Gaming.ToggleShortcut,lParam)) { _viewModel.Gaming.Toggle(); handled = true; }
        if (wParam.ToInt32() == CloseLensHotkey && MatchesShortcut(_viewModel.Gaming.CloseShortcut,lParam)) { _viewModel.Gaming.Stop(); handled = true; }
        return IntPtr.Zero;
    }
    private static bool MatchesShortcut(KeyboardShortcut? shortcut,IntPtr message)
    {
        var value=unchecked((ulong)message.ToInt64());
        return shortcut is not null && shortcut.Modifiers == (value & 0xFFFF) && shortcut.VirtualKey == ((value >> 16) & 0xFFFF);
    }
    [DllImport("user32.dll",SetLastError=true)] private static extern bool RegisterHotKey(IntPtr h,int id,uint modifiers,uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h,int id);

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
