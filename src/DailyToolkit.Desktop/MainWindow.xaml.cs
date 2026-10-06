using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DailyToolkit.Core.Gaming;
using DailyToolkit.Desktop.Gaming;
using System.Windows.Navigation;
using System.Windows.Threading;
using DailyToolkit.Desktop.Presentation;
using Microsoft.Win32;

namespace DailyToolkit.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _closing;
    private readonly DispatcherTimer _clockTimer;
    private int _ticks;
    private LensShortcutController? _shortcuts;
    private const string HotkeyConflictNotice="快捷键注册失败，可能已被其他程序占用。";
    private Point _navigationDragStart;
    private NavigationItem? _navigationDragItem;
    private int _pageTransitionGeneration;
    private Storyboard? _pageTransitionStoryboard;
    private bool _pageTransitionNamesRegistered;

    public MainWindow(MainViewModel viewModel, bool enableShortcuts = true)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.ApplyTheme(Resources);
        FavoritesIcon.Data=Controls.PageTransitionGeometry.CreateIcon(false);
        SettingsIcon.Data=Controls.PageTransitionGeometry.CreateIcon(true);
        SourceInitialized += (_, _) =>
        {
            if (!enableShortcuts) return;
            var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (source is null) return;
            InitializeRuntime(source);
            _shortcuts = new(source);
            _shortcuts.ToggleRequested += _viewModel.Gaming.Toggle;
            _shortcuts.StartRequested += _viewModel.Gaming.Start;
            _shortcuts.StopRequested += _viewModel.Gaming.Stop;
            RegisterLensHotkey();
        };
        _viewModel.Gaming.PropertyChanged += OnGamingStateChanged;
        _viewModel.PropertyChanged += OnAppSettingsChanged;
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            _viewModel.UpdateClock();
            if (++_ticks % 3 == 0) _viewModel.QueueLocalRefresh();
        };
        Loaded += (_, _) => StartLocalRuntime();
        SizeChanged += (_, _) => { FinishPageTransition(); _viewModel.SetViewportWidth(ActualWidth); };
        StateChanged += (_,_) =>
        {
            MaximizeGlyph.Text=WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            MaximizeButton.ToolTip=WindowState == WindowState.Maximized ? "还原" : "最大化";
            System.Windows.Automation.AutomationProperties.SetName(MaximizeButton,(string)MaximizeButton.ToolTip);
            if (WindowState == WindowState.Minimized && _viewModel.MinimizeToTray && !_exitRequested) HideToTray();
            else if (WindowState != WindowState.Minimized) _lastVisibleState=WindowState;
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _clockTimer.Stop();
            DisposeRuntime();
            FinishPageTransition();
            _shortcuts?.Dispose();
            _viewModel.Gaming.PropertyChanged -= OnGamingStateChanged;
            _viewModel.PropertyChanged -= OnAppSettingsChanged;
            _viewModel.Dispose();
            if (enableShortcuts) Application.Current?.Shutdown();
        };
    }

    internal FrameworkElement PreviewContent => RootContent;
    internal Task WaitForLensPreviewAsync() => LensPhotoPreview.IsVisible ? LensPhotoPreview.Ready : Task.CompletedTask;
    private void PreviewStar(object sender,RoutedEventArgs e) => SwitchPage(true,false,FavoritesButton,() => { });
    private void PreviewGear(object sender,RoutedEventArgs e) => SwitchPage(true,true,SettingsButton,() => { });

    private void ToggleFavorites(object sender,RoutedEventArgs e) => SwitchFavorites(!_viewModel.IsFavorites);
    private void ExitFavorites(object sender,RoutedEventArgs e) => SwitchFavorites(false);
    private void ToggleSettings(object sender,RoutedEventArgs e)
    {
        var opening=!_viewModel.IsSettings;
        SwitchPage(opening,true,SettingsButton,() =>
        {
            if (opening) _viewModel.OpenSettingsCommand.Execute(null); else _viewModel.ExitSettingsCommand.Execute(null);
        });
    }
    private void SwitchFavorites(bool opening)
    {
        if (_viewModel.IsFavorites == opening) return;
        SwitchPage(opening,false,FavoritesButton,() =>
        {
            if (opening) _viewModel.OpenFavoritesCommand.Execute(null); else _viewModel.ExitFavoritesCommand.Execute(null);
        });
    }

    private void SwitchPage(bool opening,bool gear,FrameworkElement button,Action switchPage)
    {
        var animate=_viewModel.PageAnimationsEnabled && SystemParameters.ClientAreaAnimation &&
            !SystemParameters.HighContrast && BodyContent.ActualWidth > 0;
        RenderTargetBitmap? snapshot=null;
        if (animate)
        {
            var dpi=VisualTreeHelper.GetDpi(BodyContent);
            snapshot=new RenderTargetBitmap((int)Math.Ceiling(BodyContent.ActualWidth*dpi.DpiScaleX),
                (int)Math.Ceiling(BodyContent.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
            snapshot.Render(BodyContent); snapshot.Freeze();
        }
        FinishPageTransition();
        switchPage();
        if (!animate) return;
        var generation=_pageTransitionGeneration;
        PageTransition.Source=snapshot;
        var center=button.TranslatePoint(new(button.ActualWidth/2,button.ActualHeight/2),BodyContent);
        var width=BodyContent.ActualWidth; var height=BodyContent.ActualHeight;
        var fullScale=Controls.PageTransitionGeometry.CoverScale(new(width,height),center,gear);
        var scale=new ScaleTransform(opening ? 0 : fullScale,opening ? 0 : fullScale);
        var rotation=new RotateTransform(opening ? 0 : 120);
        var transforms=new TransformGroup();
        transforms.Children.Add(scale); transforms.Children.Add(rotation);
        var translation=new TranslateTransform(center.X,center.Y); translation.Freeze();
        transforms.Children.Add(translation);
        var silhouette=Controls.PageTransitionGeometry.Create(gear,transforms);
        if (opening)
        {
            var outside=new GeometryGroup { FillRule=FillRule.EvenOdd };
            var bounds=new RectangleGeometry(new Rect(0,0,width,height)); bounds.Freeze();
            outside.Children.Add(bounds); outside.Children.Add(silhouette);
            PageTransition.Clip=outside;
        }
        else PageTransition.Clip=silhouette;
        PageTransition.Visibility=Visibility.Visible;
        // Keep the filled outline immutable and animate its render transform, so its
        // expanding bounds do not make the layout system measure the path every frame.
        var outline=Controls.PageTransitionGeometry.Create(gear,Transform.Identity); outline.Freeze();
        PageTransitionShape.Data=outline;
        PageTransitionShape.RenderTransform=transforms;
        PageTransitionCover.Visibility=Visibility.Visible;
        PageTransitionCover.Opacity=opening ? 1 : 0;
        NavigationPanel.IsHitTestVisible=MainContent.IsHitTestVisible=false;
        RegisterName("PageTransitionScale",scale); RegisterName("PageTransitionRotation",rotation);
        _pageTransitionNamesRegistered=true;
        var storyboard=_pageTransitionStoryboard=new Storyboard();
        var easing=new SineEase { EasingMode=EasingMode.EaseInOut }; easing.Freeze();
        void Animate(DependencyObject target,DependencyProperty property,double from,double to,int duration,int delay=0)
        {
            var animation=new DoubleAnimation(from,to,TimeSpan.FromMilliseconds(duration))
            { BeginTime=TimeSpan.FromMilliseconds(delay),EasingFunction=easing };
            // Resolve transforms by name: a Freezable used as a timeline target can be cloned
            // when the storyboard creates its clocks, leaving the displayed shape unchanged.
            if (target == scale) Storyboard.SetTargetName(animation,"PageTransitionScale");
            else if (target == rotation) Storyboard.SetTargetName(animation,"PageTransitionRotation");
            else Storyboard.SetTarget(animation,target);
            Storyboard.SetTargetProperty(animation,new PropertyPath(property));
            storyboard.Children.Add(animation);
        }
        // Blend the button color into the destination while expanding, and the
        // outgoing page back into that same color while retracting. All clocks
        // move together; the destination stays live behind the single snapshot.
        const int duration=460;
        Animate(scale,ScaleTransform.ScaleXProperty,opening ? 0 : fullScale,opening ? fullScale : 0,duration);
        Animate(scale,ScaleTransform.ScaleYProperty,opening ? 0 : fullScale,opening ? fullScale : 0,duration);
        Animate(rotation,RotateTransform.AngleProperty,opening ? 0 : 120,opening ? 120 : 0,duration);
        Animate(PageTransitionCover,OpacityProperty,opening ? 1 : 0,opening ? 0 : 1,duration);
        storyboard.Completed += (_,_) =>
        {
            if (generation == _pageTransitionGeneration) FinishPageTransition();
        };
        storyboard.Begin(this,isControllable:true);
    }

    internal void SeekPageTransition(TimeSpan offset) => _pageTransitionStoryboard?.SeekAlignedToLastTick(this,offset,TimeSeekOrigin.BeginTime);

    private void FinishPageTransition()
    {
        ++_pageTransitionGeneration;
        _pageTransitionStoryboard?.Remove(this); _pageTransitionStoryboard=null;
        if (_pageTransitionNamesRegistered)
        {
            UnregisterName("PageTransitionScale"); UnregisterName("PageTransitionRotation");
            _pageTransitionNamesRegistered=false;
        }
        PageTransition.Visibility=PageTransitionCover.Visibility=Visibility.Collapsed;
        PageTransition.Source=null; PageTransition.Clip=null; PageTransitionShape.Data=null;
        PageTransitionShape.RenderTransform=Transform.Identity;
        NavigationPanel.IsHitTestVisible=MainContent.IsHitTestVisible=true;
    }

    private void OnAppSettingsChanged(object? sender,PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.Page) && _viewModel.IsSettings) SettingsScroll.ScrollToTop();
        if (e.PropertyName == nameof(MainViewModel.PageAnimationsEnabled) && !_viewModel.PageAnimationsEnabled) FinishPageTransition();
        if (e.PropertyName is nameof(MainViewModel.Theme) or nameof(MainViewModel.AnimationColor)) _viewModel.ApplyTheme(Resources);
    }

    private void MinimizeWindow(object sender,RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void ToggleMaximizeWindow(object sender,RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this); else SystemCommands.MaximizeWindow(this);
    }
    private void CloseWindow(object sender,RoutedEventArgs e) => Close();

    private void NavigationDragStart(object sender,MouseButtonEventArgs e)
    {
        _navigationDragItem=null;
        if (_viewModel.IsSettings) return;
        _navigationDragStart=e.GetPosition(this);
        _navigationDragItem=((FrameworkElement)sender).DataContext as NavigationItem;
    }
    private void NavigationDragMove(object sender,MouseEventArgs e)
    {
        if (_viewModel.IsSettings || e.LeftButton != MouseButtonState.Pressed || _navigationDragItem is not { } item) return;
        var point=e.GetPosition(this);
        if (Math.Abs(point.X-_navigationDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y-_navigationDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _navigationDragItem=null;
        DragDrop.DoDragDrop((DependencyObject)sender,new DataObject(typeof(NavigationItem),item),DragDropEffects.Move);
    }
    private void NavigationDragOver(object sender,DragEventArgs e)
    {
        e.Effects=!_viewModel.IsSettings && e.Data.GetData(typeof(NavigationItem)) is NavigationItem item &&
            ((FrameworkElement)sender).DataContext is NavigationItem target && item != target ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled=true;
    }
    private void NavigationDrop(object sender,DragEventArgs e)
    {
        var row=(FrameworkElement)sender;
        if (e.Data.GetData(typeof(NavigationItem)) is NavigationItem item && row.DataContext is NavigationItem target)
            _viewModel.MoveNavigationItem(item.Id,target.Id,e.GetPosition(row).Y >= row.ActualHeight/2);
        e.Handled=true;
    }

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
        if (!_exitRequested && _viewModel.CloseToTray && HideToTray()) { e.Cancel=true; return; }
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

    private async void SaveReport(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存电脑信息", FileName = $"DailyToolkit-report-{DateTime.Now:yyyyMMdd-HHmm}.json",
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
            Trace.WriteLine(exception);
            _viewModel.Notice = "无法保存报告，请选择其他位置。";
        }
    }

    internal void ShowPreviewDetails(bool software)
    {
        if (_viewModel.ShowScreenLens) { UpdateLayout(); return; }
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
        catch (ExternalException exception) { Trace.WriteLine(exception); _viewModel.Notice = "剪贴板正忙，请稍后再试。"; }
    }

    private void OpenRepository(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Win32Exception exception) { Trace.WriteLine(exception); _viewModel.Notice = "没有打开浏览器。"; }
        e.Handled = true;
    }
}
