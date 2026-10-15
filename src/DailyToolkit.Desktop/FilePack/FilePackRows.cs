using System.IO;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Desktop.Presentation;

namespace DailyToolkit.Desktop.FilePack;

public sealed record FilePackChoice<T>(string Name, T Value);

public sealed class FilePackEntryViewModel : ObservableObject
{
    private PackEntry _entry;
    private string _pathDraft;
    public FilePackEntryViewModel(PackEntry entry) { _entry = entry; _pathDraft = entry.ArchivePath; }
    public PackEntry Entry => _entry;
    public Guid Id => _entry.Id;
    public string Name => Path.GetFileName(_entry.ArchivePath.TrimEnd('/'));
    public string SourcePath => string.IsNullOrEmpty(_entry.SourcePath) ? "包内文件夹" : _entry.SourcePath;
    public string Kind => _entry.IsDirectory ? "文件夹" : "文件";
    public string SizeText => _entry.IsDirectory ? "—" : FilePackViewModel.FormatBytes(_entry.Size);
    public string ArchivePath => _entry.ArchivePath;
    public string PathDraft { get => _pathDraft; set => Set(ref _pathDraft, value); }
    internal void Update(PackEntry entry) { _entry = entry; _pathDraft = entry.ArchivePath; Notify(""); }
    internal void ResetDraft() { _pathDraft = _entry.ArchivePath; Notify(nameof(PathDraft)); }
}

public sealed class FilePackTaskViewModel : ObservableObject
{
    private PackTaskInfo _info;
    public FilePackTaskViewModel(PackTaskInfo info) => _info = info;
    public PackTaskInfo Info => _info;
    public Guid TaskId => _info.TaskId;
    public string Name => _info.Snapshot.Name;
    public string ModeText => _info.Snapshot.Options.Mode == PackMode.External ? "外部软件" : "内置压缩";
    public string FormatText => _info.Snapshot.Options.Mode == PackMode.External ? "" : _info.Snapshot.Options.Format switch
    { PackFormat.SevenZip => "7z", PackFormat.Tar => "TAR", _ => "ZIP" };
    public string ConfigurationText => string.IsNullOrEmpty(FormatText) ? ModeText : $"{ModeText} · {FormatText}" +
        (_info.Snapshot.Options.Format == PackFormat.Tar ? " · 仅归档" : " · " + (_info.Snapshot.Options.Compression switch
        { PackCompression.Store => "不压缩", PackCompression.Fast => "快速", PackCompression.Maximum => "最大", _ => "标准" }));
    public string StateText => _info.State switch
    {
        PackState.Queued => "等待执行", PackState.Collecting => "检查文件", PackState.Copying => "正在准备",
        PackState.Compressing => "正在压缩", PackState.Verifying => "正在校验", PackState.AwaitingExternal => "等待外部操作",
        PackState.Completed => "已完成", PackState.Cancelled => "已取消", PackState.Failed => "失败", PackState.Interrupted => "已中断", _ => "准备中"
    };
    public double Percent => Math.Clamp(_info.Percent, 0, 100);
    public string ProgressText => $"{Percent:0}% · {FilePackViewModel.FormatBytes(_info.ProcessedBytes)} · {_info.ProcessedFiles} 项";
    public string Location => _info.Snapshot.Options.Mode == PackMode.External ? _info.WorkspacePath :
        string.IsNullOrEmpty(_info.OutputPath) ? FilePackPaths.GetOutputPath(_info.Snapshot.Options) : _info.OutputPath;
    public string Error
    {
        get
        {
            var text = _info.Error.Split('\n', 2)[0].Trim();
            var sentence = text.IndexOf('。');
            if (sentence >= 0 && sentence < 320) text = text[..(sentence + 1)];
            return text.Length <= 320 ? text : text[..320] + "…";
        }
    }
    public string ErrorDetails => _info.Error;
    public bool HasErrorDetails => _info.Error.Length > Error.Length;
    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool IsQueued => _info.State == PackState.Queued;
    public bool IsRunning => _info.State is PackState.Collecting or PackState.Copying or PackState.Compressing or PackState.Verifying;
    public bool CanCancel => IsQueued || IsRunning || IsExternalReady;
    public bool CanRetry => _info.State is PackState.Failed or PackState.Cancelled or PackState.Interrupted;
    public bool IsExternalReady => _info.State == PackState.AwaitingExternal;
    public string CompleteLabel => _info.Snapshot.Options.KeepWorkspace ? "完成并保留" : "完成并清理";
    public string AlternativeCompleteLabel => _info.Snapshot.Options.KeepWorkspace ? "完成并清理" : "完成并保留";
    public bool CanCleanup => !CanCancel && !IsExternalReady && !string.IsNullOrEmpty(_info.WorkspacePath);
    public bool CanRemove => !CanCancel && !IsExternalReady && string.IsNullOrEmpty(_info.WorkspacePath);
    public bool CanOpen => !string.IsNullOrEmpty(Location) && !IsQueued;
    public string CreatedText => _info.Snapshot.CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
    public string Details => string.Join(System.Environment.NewLine, _info.Snapshot.Entries.Select(entry =>
        $"{entry.ArchivePath} ← {(string.IsNullOrEmpty(entry.SourcePath) ? "包内文件夹" : entry.SourcePath)}"));
    internal void Update(PackTaskInfo info) { _info = info; Notify(""); }
}
