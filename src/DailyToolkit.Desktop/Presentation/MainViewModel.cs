using System.ComponentModel;
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

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IEnvironmentProbe _probe;
    private readonly DisplayInfo _display;
    private readonly ILocalStatusProbe _localStatus;
    private readonly TimeProvider _clock;
    private readonly FavoritesStore _favoritesStore;
    private readonly HashSet<string> _favoriteIds;
    private readonly CancellationTokenSource _lifetime = new();
    private MachineReport _report;
    private string _page = "home";
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
        TimeProvider? clock = null, FavoritesStore? favoritesStore = null, GamingPreferencesStore? gamingPreferencesStore = null)
    {
        _probe = probe;
        _display = display;
        _localStatus = localStatus ?? new WindowsLocalStatusProbe();
        _clock = clock ?? TimeProvider.System;
        _favoritesStore = favoritesStore ?? new();
        _favoriteIds = _favoritesStore.Load();
        Gaming = new(gamingPreferencesStore);
        Gaming.IsFavorite = _favoriteIds.Contains("screen-lens");
        _now = _clock.GetLocalNow();
        _report = new()
        {
            System = new("读取中…", "", 0, "—", "—", System.Environment.Version.ToString(), ""),
            Cpu = new("读取中…", 1, null, []), Memory = new(null, null), Display = display,
            Power = new(null, null)
        };
        _softwareView = CreateSoftwareView();
        NavigateCommand = new(parameter => Page = parameter as string ?? "home");
        RefreshCommand = new(_ =>
        {
            if (ShowScreenLens) Gaming.RefreshMonitors(); else _ = InitializeAsync();
        }, () => ShowScreenLens ? Gaming.CanConfigure : !IsRefreshing && !_isReadingStatus);
        Gaming.PropertyChanged += OnGamingChanged;
    }

    private void OnGamingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GamingViewModel.IsActive)) RefreshCommand.Refresh();
        if (e.PropertyName != nameof(GamingViewModel.IsFavorite)) return;
        if (Gaming.IsFavorite) _favoriteIds.Add("screen-lens"); else _favoriteIds.Remove("screen-lens");
        if (!_favoritesStore.Save(_favoriteIds)) Notice="收藏已在本次运行中修改，但没有保存成功。";
        Notify(nameof(HasFavorites)); Notify(nameof(ShowScreenLens)); Notify(nameof(ShowFavoritesEmpty)); Notify(nameof(RefreshText));
        RefreshCommand.Refresh();
    }

    public RelayCommand NavigateCommand { get; }
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
        set { if (Set(ref _page, value is "tools" ? "daily" : value is "daily" or "gaming" or "favorites" ? value : "home")) { Notify(""); RefreshCommand.Refresh(); } }
    }
    public bool IsHome => Page == "home";
    public bool IsDaily => Page == "daily";
    public bool IsTools => IsDaily;
    public bool IsGaming => Page == "gaming";
    public bool IsFavorites => Page == "favorites";
    public bool HasFavorites => Gaming.IsFavorite;
    public bool ShowScreenLens => IsGaming || (IsFavorites && HasFavorites);
    public bool ShowFavoritesEmpty => IsFavorites && !HasFavorites;
    public string PageTitle => IsGaming ? "游戏" : IsDaily ? "日常" : IsFavorites ? "收藏夹" : "首页";
    public string PageDescription => IsGaming ? "玩游戏时用的小工具。" : IsDaily ? "日常用的小工具。" : IsFavorites ? "常用的放在这里。" : "今天和这台电脑的状态。";
    public string RefreshText => ShowScreenLens ? "刷新屏幕" : "刷新";
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
        _ => "基础信息已就绪"
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
        Application = "DailyToolkit", Version = "0.3.9", FirstFrameMilliseconds = _firstFrameMilliseconds,
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
    public void Dispose() { Gaming.PropertyChanged -= OnGamingChanged; Gaming.Dispose(); _lifetime.Cancel(); _lifetime.Dispose(); }

    private static string FormatBytes(long? bytes) => bytes is null ? "未知" :
        bytes >= 1024L * 1024 * 1024 ? $"{bytes.Value / (1024d * 1024 * 1024):0.#} GB" :
        $"{bytes.Value / (1024d * 1024):0.#} MB";

    private static string FormatRate(double? bytes) => bytes is null ? "—" :
        bytes >= 1024 * 1024 ? $"{bytes.Value / (1024 * 1024):0.#} MB/s" : $"{bytes.Value / 1024:0.#} KB/s";
}
