using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using DailyToolkit.Core.Environment;
using DailyToolkit.Desktop.Environment;
using DailyToolkit.Desktop.Gaming;

namespace DailyToolkit.Desktop.Presentation;

public sealed record InformationRow(string Label, string Value);
public sealed record RuntimeRow(string Name, string Version, string Status);

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IEnvironmentProbe _probe;
    private readonly DisplayInfo _display;
    private readonly ILocalStatusProbe _localStatus;
    private readonly TimeProvider _clock;
    private readonly FavoritesStore _favoritesStore;
    private readonly HashSet<string> _favoriteIds;
    private readonly CancellationTokenSource _lifetime = new();
    private MachineReport _report;
    private string _page = "screen-lens";
    private bool _favoritesOpen;
    private string _pageBeforePanel="screen-lens";
    private bool _settingsOpen;
    private readonly AppPreferencesStore _appPreferencesStore;
    private AppPreferences _appPreferences;
    private string _settingsPage="settings-theme";
    private readonly NavigationItem[] _settingsNavigation=[
        new("settings-theme","主题配色","\uE790"),
        new("settings-runtime","软件运行","\uE7E8"),
        new("settings-computer","电脑详情","\uE770"),
        new("settings-about","关于","\uE946")];
    private readonly NavigationOrderStore _navigationStore;
    private readonly List<string> _navigationOrder;
    // Each installed tool gets one entry here; favorites reuse the same entries and tool state.
    private readonly NavigationItem[] _navigation=[new("screen-lens","屏幕局部放大","\uE71E",canToggle:true)];
    private string _softwareSearch = "";
    private string _notice = "";
    private bool _isRefreshing;
    private double _viewportWidth = 1040;
    private double? _firstFrameMilliseconds;
    private ICollectionView _softwareView;
    private DateTimeOffset _now;
    private DateTimeOffset? _lastWeatherRead;
    private NetworkInfo _network = NetworkInfo.Unknown;
    private WeatherInfo _weather = WeatherInfo.Unavailable("正在读取 Windows 天气");
    private bool _isReadingStatus;

    public MainViewModel(IEnvironmentProbe probe, DisplayInfo display, ILocalStatusProbe? localStatus = null,
        TimeProvider? clock = null, FavoritesStore? favoritesStore = null, GamingPreferencesStore? gamingPreferencesStore = null,
        NavigationOrderStore? navigationStore = null, AppPreferencesStore? appPreferencesStore = null,
        Runtime.IStartupRegistration? startupRegistration = null)
    {
        _probe = probe;
        _display = display;
        _localStatus = localStatus ?? new WindowsLocalStatusProbe();
        _clock = clock ?? TimeProvider.System;
        _favoritesStore = favoritesStore ?? new();
        _favoriteIds = _favoritesStore.Load();
        _navigationStore=navigationStore ?? new();
        _navigationOrder=_navigationStore.Load().ToList();
        _appPreferencesStore=appPreferencesStore ?? new();
        _appPreferences=_appPreferencesStore.Load();
        InitializeAppearance();
        InitializeStartup(startupRegistration);
        foreach (var item in _navigation) if (!_navigationOrder.Contains(item.Id)) _navigationOrder.Add(item.Id);
        Gaming = new(gamingPreferencesStore);
        _navigation[0].IsEnabled=Gaming.IsEnabled;
        Gaming.IsFavorite = _favoriteIds.Contains("screen-lens");
        _now = _clock.GetLocalNow();
        _report = new()
        {
            System = new("读取中…", "", 0, "—", "—", System.Environment.Version.ToString(), ""),
            Cpu = new("读取中…", 1, null, []), Memory = new(null, null), Display = display,
            Power = new(null, null)
        };
        _softwareView = CreateSoftwareView();
        NavigateCommand = new(parameter => Page = parameter is NavigationItem item ? item.Id : parameter as string ?? "screen-lens");
        ToggleToolCommand=new(parameter => { if (parameter is NavigationItem { Id:"screen-lens",CanToggle:true }) Gaming.IsEnabled=!Gaming.IsEnabled; });
        OpenFavoritesCommand=new(_ => OpenFavorites());
        ExitFavoritesCommand=new(_ => ExitFavorites());
        OpenSettingsCommand=new(_ => OpenSettings());
        ExitSettingsCommand=new(_ => ExitSettings());
        MoveUpCommand=new(parameter => MoveBy(parameter,-1),() => !IsSettings && NavigationItems.Count > 1);
        MoveDownCommand=new(parameter => MoveBy(parameter,1),() => !IsSettings && NavigationItems.Count > 1);
        RefreshCommand = new(_ =>
        {
            if (IsSettings && !IsHome) return;
            if (ShowScreenLens) Gaming.RefreshMonitors(); else _ = InitializeAsync();
        }, () => (!IsSettings || IsHome) && (ShowScreenLens ? Gaming.CanConfigure : !IsRefreshing && !_isReadingStatus));
        Gaming.PropertyChanged += OnGamingChanged;
        RefreshNavigation();
        NotifyNavigation();
    }

    private void OnGamingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GamingViewModel.IsActive)) { RefreshCommand.Refresh(); Notify(nameof(FooterStatusText)); }
        if (e.PropertyName == nameof(GamingViewModel.IsEnabled)) { _navigation[0].IsEnabled=Gaming.IsEnabled; Notify(nameof(FooterStatusText)); }
        if (e.PropertyName != nameof(GamingViewModel.IsFavorite)) return;
        if (Gaming.IsFavorite) _favoriteIds.Add("screen-lens"); else _favoriteIds.Remove("screen-lens");
        if (!_favoritesStore.Save(_favoriteIds)) Notice="收藏未能保存，退出后会丢失。";
        RefreshNavigation();
        if (IsFavorites && !NavigationItems.Any(item => item.Id == Page)) _page=NavigationItems.FirstOrDefault()?.Id ?? "favorites-empty";
        NotifyNavigation();
        RefreshCommand.Refresh();
    }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand ToggleToolCommand { get; }
    public RelayCommand OpenFavoritesCommand { get; }
    public RelayCommand ExitFavoritesCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand ExitSettingsCommand { get; }
    public RelayCommand MoveUpCommand { get; }
    public RelayCommand MoveDownCommand { get; }
    public ObservableCollection<NavigationItem> NavigationItems { get; } = [];
    public GamingViewModel Gaming { get; }
    public RelayCommand RefreshCommand { get; }
    public Task CurrentProbeTask { get; private set; } = Task.CompletedTask;
    public Task CurrentLocalStatusTask { get; private set; } = Task.CompletedTask;
    public Task PendingWork => Task.WhenAll(CurrentProbeTask, CurrentLocalStatusTask);
    public MachineReport Report => _report;
    public ICollectionView SoftwareView => _softwareView;

    public string Page
    {
        get => _page;
        set
        {
            if (value == "settings-animation") value="settings-theme";
            if (value is "settings-background" or "settings-startup") value="settings-runtime";
            if (value is "computer" or "home" or "tools") value="settings-computer";
            if (value == "favorites") { OpenFavorites(); return; }
            if (value == "settings" || _settingsNavigation.Any(item => item.Id == value))
            {
                OpenSettings();
                if (value != "settings") { _settingsPage=value; _page=value; NotifyNavigation(); }
                return;
            }
            if (IsSettings) ExitSettings();
            var id=value == "gaming" ? "screen-lens" : _navigation.Any(item => item.Id == value) ? value : "screen-lens";
            if (IsFavorites && !_favoriteIds.Contains(id)) ExitFavorites();
            _page=id;
            NotifyNavigation();
        }
    }
    public bool IsHome => IsSettings && Page == "settings-computer";
    public bool IsFavorites => _favoritesOpen && !IsSettings;
    public bool IsSettings => _settingsOpen;
    public bool ShowSettingsContent => IsSettings && !IsHome;
    public bool ShowThemeSettings => IsSettings && Page == "settings-theme";
    public bool ShowRuntimeSettings => IsSettings && Page == "settings-runtime";
    public bool ShowAboutSettings => IsSettings && Page == "settings-about";
    public bool HasFavorites => _navigation.Any(item => _favoriteIds.Contains(item.Id));
    public bool ShowScreenLens => Page == "screen-lens";
    public bool ShowFavoritesEmpty => IsFavorites && !HasFavorites;
    public bool ShowAllTools => !IsFavorites && !IsSettings;
    public bool ShowRefresh => !IsSettings || IsHome;
    public string FavoritesToggleHint => IsFavorites ? "关闭收藏夹" : "打开收藏夹";
    public string SettingsToggleHint => IsSettings ? "关闭软件设置" : "打开软件设置";
    public string PageTitle => IsSettings ? _settingsNavigation.First(item => item.Id == Page).Name : ShowFavoritesEmpty ? "收藏夹" : _navigation.First(item => item.Id == Page).Name;
    public string RefreshText => ShowScreenLens ? "刷新屏幕" : "刷新";
    public string FooterStatusText => IsHome ? StatusText : IsSettings || ShowFavoritesEmpty ? "" : !Gaming.IsEnabled ? "功能已停用" : Gaming.IsActive ? "放大运行中" : "未开启";
    public string FooterSourceText => IsHome ? LocalSourceText : IsSettings || ShowFavoritesEmpty ? "" : "本机显示";
    public bool PageAnimationsEnabled
    {
        get => _appPreferences.PageAnimationsEnabled;
        set
        {
            if (value == PageAnimationsEnabled) return;
            _appPreferences=_appPreferences with { PageAnimationsEnabled=value };
            if (!_appPreferencesStore.Save(_appPreferences)) Notice="界面设置未能保存，退出后会丢失。";
            Notify();
        }
    }
    public double AnimationSpeed
    {
        get => _appPreferences.AnimationSpeed;
        set
        {
            if (!double.IsFinite(value)) return;
            var speed=AppPreferences.NormalizeAnimationSpeed(value);
            if (speed == AnimationSpeed) return;
            _appPreferences=_appPreferences with { AnimationSpeed=speed }; SaveAppPreferences();
            Notify(); Notify(nameof(AnimationSpeedText));
        }
    }
    public string AnimationSpeedText => $"{AnimationSpeed:0.0}×";
    internal TimeSpan AnimationDuration(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds/AnimationSpeed);

    private void OpenFavorites()
    {
        if (IsFavorites) return;
        if (!IsSettings) _pageBeforePanel=Page;
        _settingsOpen=false;
        _favoritesOpen=true;
        _page=_pageBeforePanel;
        RefreshNavigation();
        if (!NavigationItems.Any(item => item.Id == Page)) _page=NavigationItems.FirstOrDefault()?.Id ?? "favorites-empty";
        NotifyNavigation();
    }

    private void ExitFavorites()
    {
        if (!IsFavorites) return;
        _favoritesOpen=false;
        _page=_pageBeforePanel;
        RefreshNavigation();
        NotifyNavigation();
    }

    private void OpenSettings()
    {
        if (IsSettings) return;
        if (!IsFavorites) _pageBeforePanel=Page;
        _favoritesOpen=false;
        _settingsOpen=true;
        _page=_settingsPage;
        RefreshNavigation(); NotifyNavigation();
    }

    private void ExitSettings()
    {
        if (!IsSettings) return;
        _settingsOpen=false;
        _page=_pageBeforePanel;
        RefreshNavigation();
        NotifyNavigation();
    }

    private void RefreshNavigation()
    {
        NavigationItems.Clear();
        if (IsSettings)
        {
            foreach (var item in _settingsNavigation) NavigationItems.Add(item);
            MoveUpCommand.Refresh(); MoveDownCommand.Refresh();
            return;
        }
        foreach (var id in _navigationOrder)
        {
            var item=_navigation.FirstOrDefault(item => item.Id == id);
            if (item is not null && (!IsFavorites || _favoriteIds.Contains(id))) NavigationItems.Add(item);
        }
        MoveUpCommand.Refresh(); MoveDownCommand.Refresh();
    }

    private void NotifyNavigation()
    {
        foreach (var item in _navigation) item.IsSelected=item.Id == Page;
        foreach (var item in _settingsNavigation) item.IsSelected=IsSettings && item.Id == Page;
        Notify(nameof(Page)); Notify(""); RefreshCommand.Refresh();
    }

    private void MoveBy(object? parameter,int offset)
    {
        if (parameter is not NavigationItem item) return;
        var index=NavigationItems.IndexOf(item);
        var target=index+offset;
        if (index >= 0 && target >= 0 && target < NavigationItems.Count)
            MoveNavigationItem(item.Id,NavigationItems[target].Id,after:offset > 0);
    }

    public bool MoveNavigationItem(string id,string targetId,bool after=false)
    {
        if (IsSettings) return false;
        if (id == targetId || !NavigationItems.Any(item => item.Id == id) || !NavigationItems.Any(item => item.Id == targetId)) return false;
        _navigationOrder.Remove(id);
        _navigationOrder.Insert(_navigationOrder.IndexOf(targetId)+(after ? 1 : 0),id);
        RefreshNavigation();
        if (!_navigationStore.Save(_navigationOrder)) Notice="排序未能保存，退出后会丢失。";
        return true;
    }
    public string TimeText => _now.ToString("HH:mm");
    public string DateText => _now.ToString("yyyy年M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
    public NetworkInfo Network => _network;
    public WeatherInfo Weather => _weather;
    public string WeatherText => Weather.Summary;
    public string WeatherNote => Weather.Available ?
        string.Join(" · ", new[] { Weather.Location, Weather.Source }.Where(text => !string.IsNullOrWhiteSpace(text))) :
        Weather.Reason ?? "Windows 天气暂时不可用";
    public string WeatherUpdatedText => Weather.UpdatedAt is { } updated
        ? $"{(Weather.FromCache && _now - updated > TimeSpan.FromHours(3) ? "缓存较旧 · " : "")}{TimeZoneInfo.ConvertTime(updated, _clock.LocalTimeZone):M月d日 HH:mm} 更新" : "";
    public string NetworkText => Network.State switch
    {
        NetworkState.Internet => "已联网", NetworkState.LocalNetwork => "仅本地网络",
        NetworkState.Disconnected => "未连接", _ => "未确认"
    };
    public string NetworkDetail => string.IsNullOrWhiteSpace(Network.ConnectionType)
        ? "Windows 网络状态" : $"{Network.ConnectionType} · Windows 状态";
    public string TrafficText => $"↓ {FormatRate(Network.ReceivedBytesPerSecond)}   ↑ {FormatRate(Network.SentBytesPerSecond)}";

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!Set(ref _isRefreshing, value)) return;
            RefreshCommand.Refresh();
            Notify("");
        }
    }

    public string Notice { get => _notice; set { if (Set(ref _notice, value)) Notify(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
    public bool HasIssues => Report.Issues.Count > 0;
    public string IssueSummary => string.Join("；", Report.Issues.Select(x => x.Message).Distinct());
    public string StatusText => IsRefreshing ? "正在读取电脑信息" : Report.Stage switch
    {
        ProbeStage.Complete => "电脑信息已更新",
        ProbeStage.Partial => "部分信息暂时不可用",
        _ => "已读取基础信息"
    };
    public string CpuName => Report.Cpu.Name;
    public string CpuDetail => Report.Cpu.PhysicalCores is int cores
        ? $"{cores} 核 · {Report.Cpu.LogicalProcessors} 个逻辑处理器"
        : $"{Report.Cpu.LogicalProcessors} 个逻辑处理器";
    public string MemoryTotal => FormatBytes(Report.Memory.TotalBytes);
    public string MemoryDetail => $"当前可用 {FormatBytes(Report.Memory.AvailableBytes)}";
    public string SystemName => Report.System.Name;
    public string SystemDetail => Report.System.Version;
    public string Architecture => Report.System.Architecture;
    public string DisplayDetail => $"{Report.Display.PixelWidth} × {Report.Display.PixelHeight} · 缩放 {Report.Display.Scale:P0}";
    public string GraphicsText => Report.Graphics.Count == 0 ? "暂未读取" :
        string.Join(" / ", Report.Graphics.Select(x => x.Name));
    public string GraphicsSummary
    {
        get
        {
            var names = Report.Graphics.Where(graphics => !graphics.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase))
                .Select(graphics => graphics.Name).ToArray();
            return names.Length == 0 ? GraphicsText : string.Join(" / ", names);
        }
    }
    public string SoftwareCount => Report.SoftwareInventoryCompleted ? Report.Software.Count.ToString() : "—";
    public string SoftwareHeader => Report.SoftwareInventoryCompleted
        ? $"已安装软件 · {Report.Software.Count}" : "已安装软件";
    public string PowerText => Report.Power.OnBattery switch
    {
        true => Report.Power.BatteryPercent is int percent ? $"使用电池 · {percent}%" : "使用电池",
        false => "已接电源",
        _ => "电源状态未知"
    };

    public AdaptationProfile Profile => AdaptationPolicy.Evaluate(Report, _viewportWidth);
    public Thickness ContentPadding => new(Profile.CompactLayout ? 22 : 32);
    public string PowerDetail => Report.Power.BatteryPercent is int percent ? $"电池电量 {percent}%" : "电量未确认";
    public string LocalSourceText => "读取 Windows 本机数据";

    public IReadOnlyList<InformationRow> ComputerSummaryRows =>
    [
        new("系统", $"{SystemName} · {SystemDetail} · {Architecture}"),
        new("处理器", $"{CpuName} · {CpuDetail}"),
        new("内存", $"{MemoryTotal} · 可用 {FormatBytes(Report.Memory.AvailableBytes)}"),
        new("显卡", GraphicsSummary)
    ];

    public IReadOnlyList<InformationRow> SystemRows =>
    [
        new("系统", $"{SystemName} · {SystemDetail}"),
        new("系统架构", $"{Report.System.Architecture} · 应用 {Report.System.ProcessArchitecture}"),
        new("处理器", CpuName), new("核心", CpuDetail),
        new("内存", $"{MemoryTotal} · 可用 {FormatBytes(Report.Memory.AvailableBytes)}"),
        new("显示", DisplayDetail), new("电源", PowerText),
        new("处理器指令", Report.Cpu.Features.Count == 0 ? "未确认" : string.Join("、", Report.Cpu.Features))
    ];
    public IReadOnlyList<InformationRow> GraphicsRows => Report.Graphics.Select(graphics =>
        new InformationRow(graphics.Name, $"驱动 {graphics.DriverVersion ?? "未知"}")).ToArray();
    public IReadOnlyList<InformationRow> VolumeRows => Report.Volumes.Select(volume =>
        new InformationRow(volume.Name, $"{FormatBytes(volume.TotalBytes)} · 可用 {FormatBytes(volume.FreeBytes)} · {volume.FileSystem}")).ToArray();
    public IReadOnlyList<RuntimeRow> RuntimeRows => Report.Runtimes.Select(runtime =>
        new RuntimeRow(runtime.Name, runtime.Version ?? "—", runtime.Status switch
        {
            RuntimeStatus.Available => "可用", RuntimeStatus.Missing => "未找到", _ => "未确认"
        })).ToArray();

    public string SoftwareSearch
    {
        get => _softwareSearch;
        set { if (Set(ref _softwareSearch, value)) _softwareView.Refresh(); }
    }

    public void SetViewportWidth(double width)
    {
        var previous = Profile.CompactLayout;
        _viewportWidth = width;
        if (Profile.CompactLayout != previous) Notify("");
    }

    public void SetFirstFrameTime(double milliseconds)
    {
        _firstFrameMilliseconds = milliseconds;
    }

    public Task InitializeAsync()
    {
        if (!CurrentProbeTask.IsCompleted) return CurrentProbeTask;
        UpdateClock();
        QueueLocalRefresh(forceWeather: true);
        CurrentProbeTask = Task.WhenAll(RefreshAsync(), CurrentLocalStatusTask);
        return CurrentProbeTask;
    }

    public void UpdateClock()
    {
        _now = _clock.GetLocalNow();
        Notify(nameof(TimeText));
        Notify(nameof(DateText));
        Notify(nameof(WeatherUpdatedText));
    }

    public void QueueLocalRefresh(bool forceWeather = false)
    {
        if (_isReadingStatus || _lifetime.IsCancellationRequested) return;
        CurrentLocalStatusTask = RefreshLocalStatusAsync(forceWeather);
    }

    private async Task RefreshLocalStatusAsync(bool forceWeather)
    {
        _isReadingStatus = true;
        RefreshCommand.Refresh();
        var token = _lifetime.Token;
        var readWeather = forceWeather || _lastWeatherRead is not { } last || _now < last || _now - last >= TimeSpan.FromMinutes(1);
        try
        {
            var networkTask = ReadNetworkSafelyAsync(token);
            var weatherTask = readWeather ? ReadWeatherSafelyAsync(token) : Task.FromResult(_weather);
            await Task.WhenAll(networkTask, weatherTask);
            token.ThrowIfCancellationRequested();
            _network = await networkTask;
            _weather = await weatherTask;
            if (readWeather) _lastWeatherRead = _now;
            foreach (var property in new[] { nameof(Network), nameof(Weather), nameof(NetworkText), nameof(NetworkDetail),
                nameof(TrafficText), nameof(WeatherText), nameof(WeatherNote), nameof(WeatherUpdatedText) }) Notify(property);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { _isReadingStatus = false; RefreshCommand.Refresh(); }
    }

    private async Task<NetworkInfo> ReadNetworkSafelyAsync(CancellationToken token)
    {
        try { return await _localStatus.ReadNetworkAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); return NetworkInfo.Unknown; }
    }

    private async Task<WeatherInfo> ReadWeatherSafelyAsync(CancellationToken token)
    {
        try { return await _localStatus.ReadWeatherAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) { System.Diagnostics.Trace.WriteLine(exception); return WeatherInfo.Unavailable("Windows 天气文字暂时不可读"); }
    }

    public string ExportJson() => JsonSerializer.Serialize(new
    {
        Application = "DailyToolkit", Version = "0.6.1", FirstFrameMilliseconds = _firstFrameMilliseconds,
        Environment = Report, Adaptation = Profile,
        Daily = new { WindowsTime = _now, Network, Weather }
    }, MachineReport.JsonOptions);

    private async Task RefreshAsync()
    {
        if (IsRefreshing || _lifetime.IsCancellationRequested) return;
        IsRefreshing = true;
        Notice = "";
        var token = _lifetime.Token;
        try
        {
            var basic = await Task.Run(() => _probe.ReadBasic(_display), token);
            Apply(basic);
            var details = await Task.Run(() => _probe.ReadDetailsAsync(basic, token), token);
            Apply(details);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine(exception);
            Apply(Report with
            {
                Stage = ProbeStage.Partial,
                Issues = Report.Issues.Concat([new ProbeIssue("检测", "部分电脑信息没有读取完成")]).ToArray()
            });
        }
        finally { IsRefreshing = false; }
    }

    private void Apply(MachineReport report)
    {
        _report = report;
        _softwareView = CreateSoftwareView();
        Notify("");
    }

    private ICollectionView CreateSoftwareView()
    {
        var view = CollectionViewSource.GetDefaultView(Report.Software);
        view.Filter = item => item is InstalledSoftwareInfo software &&
            (string.IsNullOrWhiteSpace(SoftwareSearch) ||
             $"{software.Name} {software.Version} {software.Publisher}".Contains(
                 SoftwareSearch.Trim(), StringComparison.CurrentCultureIgnoreCase));
        return view;
    }

    public void CancelPending() => _lifetime.Cancel();
    public void Dispose() { _startupDisposed=true; Gaming.PropertyChanged -= OnGamingChanged; Gaming.Dispose(); _lifetime.Cancel(); _lifetime.Dispose(); }

    private static string FormatBytes(long? bytes) => bytes is null ? "未知" :
        bytes >= 1024L * 1024 * 1024 ? $"{bytes.Value / (1024d * 1024 * 1024):0.#} GB" :
        $"{bytes.Value / (1024d * 1024):0.#} MB";

    private static string FormatRate(double? bytes) => bytes is null ? "—" :
        bytes >= 1024 * 1024 ? $"{bytes.Value / (1024 * 1024):0.#} MB/s" : $"{bytes.Value / 1024:0.#} KB/s";
}
