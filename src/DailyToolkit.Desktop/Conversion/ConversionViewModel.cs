using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using DailyToolkit.Core.Conversion;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.Conversion;

public sealed class ConversionItem(string source) : ObservableObject
{
    private string _state = "等待", _detail = "";
    public string Source { get; } = source;
    public string Name => Path.GetFileName(Source);
    public string State { get => _state; internal set => Set(ref _state, value); }
    public string Detail { get => _detail; internal set => Set(ref _detail, value); }
    public IReadOnlyList<string> Outputs { get; internal set; } = [];
}

public sealed class ConversionViewModel : ObservableObject, IDisposable
{
    private readonly ConversionSettingsStore _store;
    private readonly ConversionRunner _runner;
    private ConversionSettings _saved, _draft;
    private CancellationTokenSource? _cancellation;
    private bool _busy, _favorite, _disposed, _saveFailed;
    private string _notice = "";
    private double _progress;
    private ConversionItem? _selected;
    private Task _pending = Task.CompletedTask;

    public ConversionViewModel(ConversionSettingsStore? store = null, IConversionBackend? backend = null)
    {
        _store = store ?? new(); _saved = _draft = _store.Load();
        Formats = ConversionFormats.Targets(_draft.Kind);
        _runner = new(backend ?? new LocalConversionBackend());
        ToggleFavoriteCommand = new(_ => IsFavorite = !IsFavorite);
        ToggleEnabledCommand = new(_ => SetEnabled(!IsEnabled));
        StartCommand = new(_ => Start(), () => CanStart);
        CancelCommand = new(_ => Cancel(), () => IsBusy && _cancellation?.IsCancellationRequested == false);
        RemoveCommand = new(_ => { if (SelectedItem is { } item) { Items.Remove(item); SelectedItem = null; Changed(); } }, () => CanEdit && SelectedItem is not null);
        ClearCommand = new(_ => { Items.Clear(); SelectedItem = null; Changed(); }, () => CanEdit && Items.Count > 0);
        SaveSettingsCommand = new(_ => SaveSettings(), () => !_disposed && !IsBusy);
        OpenOutputCommand = new(_ => OpenOutput(), () => !IsBusy && HasLocalOutput());
    }

    public ObservableCollection<ConversionItem> Items { get; } = [];
    public IReadOnlyList<ConversionCategory> Categories => ConversionFormats.Categories;
    public IReadOnlyList<ConversionFormat> Formats { get; private set; }
    public IReadOnlyList<int> PdfResolutions { get; } = [72, 96, 150, 200, 300];
    public RelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand ToggleEnabledCommand { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RemoveCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }
    public RelayCommand OpenOutputCommand { get; }
    public bool IsEnabled => _draft.IsEnabled;
    public bool IsFavorite { get => _favorite; set { if (Set(ref _favorite, value)) Notify(nameof(FavoriteHint)); } }
    public string FavoriteHint => IsFavorite ? "取消收藏" : "收藏这个工具";
    public bool IsBusy => _busy;
    public bool CanEdit => !_disposed && IsEnabled && !IsBusy;
    public bool CanStart => CanEdit && Items.Any(i => i.State != "完成") && !string.IsNullOrWhiteSpace(OutputDirectory);
    public bool IsIdleForMemoryCollection => !IsEnabled && !IsBusy && !_saveFailed;
    public Task PendingWork => _pending;
    public double Progress => _progress;
    public string Notice { get => _notice; private set { Set(ref _notice, value); Notify(nameof(HasNotice)); } }
    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);
    public string Status => IsBusy ? _cancellation?.IsCancellationRequested == true ? "正在停止转换" : "正在转换" : !IsEnabled ? "功能已停用" : $"{Items.Count} 个文件";
    public bool IsPdf => Kind == ConversionKind.Pdf;
    public bool ShowImageQuality => (Kind == ConversionKind.Image || Kind == ConversionKind.Pdf) && (Target == "jpg" || Target == "webp");
    public string FormatHint => Kind switch
    {
        ConversionKind.Image => "PNG、JPG、BMP、TIFF、静态 GIF。WebP 需要本地 FFmpeg。JPG、BMP 和 PDF 使用白底。",
        ConversionKind.Pdf => "每页保存为一张图片，保留页面顺序。",
        ConversionKind.Media => "使用系统编解码器，或已选择的本地 FFmpeg。视频转换不保留字幕轨道。",
        _ => "需要本地 LibreOffice。文字、表格和演示文稿分别转换；CSV 仅保存一张表。"
    };
    public ConversionItem? SelectedItem { get => _selected; set { if (Set(ref _selected, value)) RemoveCommand.Refresh(); } }
    public ConversionKind Kind
    {
        get => _draft.Kind;
        set
        {
            if (IsBusy || value == Kind || !Enum.IsDefined(value)) return;
            _draft = _draft with { Kind = value, Target = ConversionFormats.Targets(value)[0].Id };
            Formats = ConversionFormats.Targets(value);
            // Keep every item; a type change must never silently discard the list.
            Changed();
        }
    }
    public string Target
    {
        get => _draft.Target;
        set { if (!IsBusy && value != Target && Formats.Any(f => f.Id == value)) { _draft = _draft with { Target = value }; Changed(); } }
    }
    public string OutputDirectory { get => _draft.OutputDirectory; set { if (!IsBusy && (value ?? "") != OutputDirectory) { _draft = _draft with { OutputDirectory = value ?? "" }; Changed(); } } }
    public int Quality { get => _draft.Options.Quality; set { if (!IsBusy && Math.Clamp(value, 1, 100) != Quality) { _draft = _draft with { Options = _draft.Options with { Quality = Math.Clamp(value, 1, 100) } }; Changed(); } } }
    public int PdfDpi { get => _draft.Options.PdfDpi; set { if (!IsBusy && Math.Clamp(value, 72, 300) != PdfDpi) { _draft = _draft with { Options = _draft.Options with { PdfDpi = Math.Clamp(value, 72, 300) } }; Changed(); } } }
    public string FFmpegPath { get => _draft.Options.FFmpegPath; set { if (!IsBusy && (value ?? "") != FFmpegPath) { _draft = _draft with { Options = _draft.Options with { FFmpegPath = value ?? "" } }; Changed(); } } }
    public string LibreOfficePath { get => _draft.Options.LibreOfficePath; set { if (!IsBusy && (value ?? "") != LibreOfficePath) { _draft = _draft with { Options = _draft.Options with { LibreOfficePath = value ?? "" } }; Changed(); } } }

    public void SetEnabled(bool enabled)
    {
        if (_disposed || enabled == IsEnabled) return;
        // A failed save retains the prior enabled state and all editable values.
        var saved = _saved with { IsEnabled = enabled };
        if (!_store.Save(saved)) { _saveFailed = true; Notice = "设置未能保存，功能状态未更改。"; Changed(); return; }
        _saveFailed = false; _saved = saved; _draft = _draft with { IsEnabled = enabled };
        if (!enabled) Cancel();
        Changed();
    }

    public void AddFiles(IEnumerable<string> paths)
    {
        if (!CanEdit) return;
        var skipped = 0;
        foreach (var path in paths.Take(101))
        {
            if (Items.Count >= 100) { Notice = "每批最多 100 个文件。"; Changed(); return; }
            try
            {
                var full = ConversionRunner.LocalPath(path);
                if (!File.Exists(full) || !ConversionFormats.Accepts(Kind, full)) { skipped++; continue; }
                if (!Items.Any(i => i.Source.Equals(full, StringComparison.OrdinalIgnoreCase))) Items.Add(new(full));
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException) { skipped++; }
        }
        Notice = skipped == 0 ? "" : $"已略过 {skipped} 个不支持的文件，请检查转换类型。";
        Changed();
    }

    public bool SaveSettings()
    {
        if (_disposed || IsBusy) return false;
        try
        {
            if (!string.IsNullOrWhiteSpace(OutputDirectory)) ConversionRunner.LocalPath(OutputDirectory);
            var engines = Kind == ConversionKind.Document ? new[] { LibreOfficePath } :
                Kind == ConversionKind.Media || Target == "webp" || Items.Any(i => i.State != "完成" && Path.GetExtension(i.Source).Equals(".webp", StringComparison.OrdinalIgnoreCase))
                    ? new[] { FFmpegPath } : [];
            foreach (var engine in engines.Where(p => !string.IsNullOrWhiteSpace(p)))
                if (!File.Exists(ConversionRunner.LocalPath(engine))) throw new FileNotFoundException("本地引擎程序不存在。");
            if (!_store.Save(_draft)) throw new IOException("设置未能保存，请检查文件权限或磁盘空间。");
            _saved = _draft; _saveFailed = false; Notice = "设置已保存。"; Changed(); return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { _saveFailed = true; Notice = e.Message; Changed(); return false; }
    }

    private void Start()
    {
        if (!CanStart) return;
        if (Kind == ConversionKind.Document && string.IsNullOrWhiteSpace(LibreOfficePath))
        { Notice = "文档转换需要本地 LibreOffice，请先在“本地引擎”中选择程序。"; return; }
        var needsFFmpeg = Kind == ConversionKind.Media && Target is "flac" or "ogg" or "mkv" or "webm" ||
            Kind == ConversionKind.Image && (Target == "webp" || Items.Any(i => i.State != "完成" && Path.GetExtension(i.Source).Equals(".webp", StringComparison.OrdinalIgnoreCase)));
        if (needsFFmpeg && string.IsNullOrWhiteSpace(FFmpegPath))
        { Notice = "此格式需要本地 FFmpeg，请先在“本地引擎”中选择程序。"; return; }
        if (!SaveSettings()) return;
        var batch = Items.Where(i => i.State != "完成").ToArray();
        var settings = _draft;
        _cancellation = new(); _busy = true; _progress = 0; Notice = ""; Changed();
        _pending = RunBatchAsync(batch, settings, _cancellation);
    }

    private async Task RunBatchAsync(ConversionItem[] batch, ConversionSettings settings, CancellationTokenSource cancellation)
    {
        var completed = 0; var failed = 0;
        try
        {
            for (var index = 0; index < batch.Length; index++)
            {
                var item = batch[index];
                if (cancellation.IsCancellationRequested) { item.State = "已取消"; continue; }
                item.State = "转换中"; item.Detail = "";
                var current = index;
                var progress = new Progress<double>(value =>
                {
                    if (ReferenceEquals(_cancellation, cancellation))
                    { _progress = (current + Math.Clamp(value, 0, 100) / 100) * 100 / batch.Length; Notify(nameof(Progress)); }
                });
                try
                {
                    item.Outputs = await _runner.RunAsync(new(item.Source, settings.OutputDirectory, settings.Kind, settings.Target, settings.Options), progress, cancellation.Token);
                    item.State = "完成"; item.Detail = string.Join("；", item.Outputs.Select(Path.GetFileName)); completed++;
                }
                catch (OperationCanceledException) { item.State = "已取消"; item.Detail = "原文件保留。"; }
                catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
                { item.State = "失败"; item.Detail = e.Message; failed++; }
                _progress = (index + 1) * 100.0 / batch.Length; Notify(nameof(Progress));
            }
            Notice = cancellation.IsCancellationRequested ? "转换已取消，已完成的文件保留。" : $"完成 {completed} 个，失败 {failed} 个。";
        }
        finally
        {
            if (ReferenceEquals(_cancellation, cancellation)) _cancellation = null;
            cancellation.Dispose(); _busy = false; Changed();
        }
    }

    public void Cancel() { _cancellation?.Cancel(); Changed(); }
    public async Task ShutdownAsync() { Cancel(); await _pending; }
    private bool HasLocalOutput()
    {
        try { return Directory.Exists(ConversionRunner.LocalPath(OutputDirectory)); }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return false; }
    }
    private void OpenOutput()
    {
        try { Process.Start(new ProcessStartInfo(ConversionRunner.LocalPath(OutputDirectory)) { UseShellExecute = true }); }
        catch (Exception e) when (e is IOException or ArgumentException or System.ComponentModel.Win32Exception) { Notice = "输出位置未能打开。"; }
    }
    private void Changed()
    {
        Notify(""); StartCommand.Refresh(); CancelCommand.Refresh(); RemoveCommand.Refresh(); ClearCommand.Refresh(); SaveSettingsCommand.Refresh(); OpenOutputCommand.Refresh();
    }
    public void Dispose() { if (_disposed) return; _disposed = true; _cancellation?.Cancel(); }
}
