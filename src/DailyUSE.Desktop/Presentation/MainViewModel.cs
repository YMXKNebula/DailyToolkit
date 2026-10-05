using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using DailyUSE.Core.Environment;
using DailyUSE.Core.Tools;

namespace DailyUSE.Desktop.Presentation;

public sealed record InformationRow(string Label, string Value);
public sealed record RuntimeRow(string Name, string Version, string Status);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IEnvironmentProbe _probe;
    private readonly DisplayInfo _display;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ToolCatalog _catalog = new();
    private MachineReport _report;
    private string _page = "home";
    private string _softwareSearch = "";
    private string _notice = "";
    private bool _isRefreshing;
    private double _viewportWidth = 1040;
    private double? _firstFrameMilliseconds;
    private ICollectionView _softwareView;

    public MainViewModel(IEnvironmentProbe probe, DisplayInfo display)
    {
        _probe = probe;
        _display = display;
        _report = new()
        {
            System = new("读取中…", "", 0, "—", "—", System.Environment.Version.ToString(), ""),
            Cpu = new("读取中…", 1, null, []), Memory = new(null, null), Display = display,
            Power = new(null, null)
        };
        _softwareView = CreateSoftwareView();
        _catalog.Register(new("computer-info", "电脑信息", "查看系统、硬件和运行环境", new()));
        NavigateCommand = new(parameter => Page = parameter as string ?? "home");
        RefreshCommand = new(_ => CurrentProbeTask = RefreshAsync(), () => !IsRefreshing);
        OpenComputerCommand = new(_ => Page = "computer");
    }

    public RelayCommand NavigateCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand OpenComputerCommand { get; }
    public Task CurrentProbeTask { get; private set; } = Task.CompletedTask;
    public MachineReport Report => _report;
    public IReadOnlyList<ToolDefinition> Tools => _catalog.Tools;
    public ICollectionView SoftwareView => _softwareView;

    public string Page
    {
        get => _page;
        set { if (Set(ref _page, value)) Notify(""); }
    }
    public bool IsHome => Page == "home";
    public bool IsTools => Page == "tools";
    public bool IsComputer => Page == "computer";
    public string PageTitle => Page switch { "tools" => "工具", "computer" => "电脑信息", _ => "首页" };
    public string PageDescription => Page switch
    {
        "tools" => "把常用的小工具放在一起。",
        "computer" => "这些信息用于选择合适的工具和运行方式。",
        _ => "工具和这台电脑的状态，都在这里。"
    };

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
    public string StatusDetail => IsRefreshing ? "可以先使用界面，详细信息会自动补齐。" :
        $"更新于 {Report.CapturedAt:HH:mm} · 详细检测 {Report.DetailReadMilliseconds / 1000:0.0} 秒";
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
    public bool ReducedEffects => Profile.ReducedEffects;
    public Thickness ContentPadding => new(Profile.CompactLayout ? 22 : 32);
    public double CardHeight => Profile.CompactLayout ? 150 : 138;
    public string LayoutText => Profile.CompactLayout ? "紧凑布局" : "标准布局";
    public string TaskLimitText => $"最多 {Profile.WorkerLimit} 个后台任务";
    public string ProfileNote => Profile.Reasons.Count > 0 ? string.Join("；", Profile.Reasons) : "按这台电脑的配置安排后台任务。";
    public string StartupText => _firstFrameMilliseconds is double time ? $"界面就绪 {time:0} ms" : "本机运行";

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
        Notify(nameof(StartupText));
    }

    public Task InitializeAsync()
    {
        if (!CurrentProbeTask.IsCompleted) return CurrentProbeTask;
        CurrentProbeTask = RefreshAsync();
        return CurrentProbeTask;
    }

    public string ExportJson() => JsonSerializer.Serialize(new
    {
        Application = "DailyUSE", Version = "0.1.0", FirstFrameMilliseconds = _firstFrameMilliseconds,
        Environment = Report, Adaptation = Profile
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
        catch (Exception)
        {
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
    public void Dispose() { _lifetime.Cancel(); _lifetime.Dispose(); }

    private static string FormatBytes(long? bytes) => bytes is null ? "未知" :
        bytes >= 1024L * 1024 * 1024 ? $"{bytes.Value / (1024d * 1024 * 1024):0.#} GB" :
        $"{bytes.Value / (1024d * 1024):0.#} MB";
}
