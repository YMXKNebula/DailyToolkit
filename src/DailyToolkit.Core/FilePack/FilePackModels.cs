using DailyToolkit.Core.Gaming;

namespace DailyToolkit.Core.FilePack;

public enum PackMode { BuiltIn, External }
public enum PackFormat { Zip, SevenZip, Tar }
public enum PackCompression { Store, Fast, Standard, Maximum }
public enum PackState { Queued, Collecting, Copying, Compressing, Verifying, AwaitingExternal, Completed, Cancelled, Failed, Interrupted }
public enum PackOutputPolicy { Fail, AutoRename, Overwrite }

public sealed record PackSourceStamp(long Length, long LastWriteUtcTicks, long CreationUtcTicks);
public sealed record PackEntry(Guid Id, string SourcePath, string ArchivePath, bool IsDirectory, long Size,
    PackSourceStamp? SourceStamp, string SourceRoot);
public sealed record PackOptions(PackMode Mode, PackFormat Format, PackCompression Compression, string OutputDirectory,
    string ArchiveName, PackOutputPolicy OutputPolicy = PackOutputPolicy.Fail, bool AllowStaging = false,
    bool KeepWorkspace = false, PackSourceStamp? ApprovedOverwriteStamp = null);
public sealed record PackSnapshot(Guid TaskId, Guid SessionId, string Name, IReadOnlyList<PackEntry> Entries,
    PackOptions Options, DateTimeOffset CreatedAt);
public sealed record PackProgress(PackState State, double Percent, long ProcessedBytes, int ProcessedFiles, string Detail = "");
public sealed record PackTaskInfo(PackSnapshot Snapshot, PackState State, double Percent = 0, long ProcessedBytes = 0,
    int ProcessedFiles = 0, string Error = "", string OutputPath = "", string WorkspacePath = "", long QueueOrder = 0,
    DateTimeOffset? FinishedAt = null)
{
    public Guid TaskId => Snapshot.TaskId;
}
public sealed record PackCollectionResult(IReadOnlyList<PackEntry> Entries, IReadOnlyList<string> Warnings);
public readonly record struct PackTaskCounts(int Running, int Queued, int Completed, int Failed, int External);
public sealed record PackDraftEntry(PackEntry Entry, string PathDraft);
public sealed record PackDraft(IReadOnlyList<PackDraftEntry> Entries, string TaskName, string ArchiveName,
    string OutputDirectory, PackMode Mode, PackFormat Format, PackCompression Compression,
    string NewFolderPath = "新建文件夹", string MoveTargetPath = "");
public sealed record PackBatch(string WorkingDirectory, IReadOnlyList<PackEntry> Entries);
public sealed record PackPlan(IReadOnlyList<PackBatch> Batches, bool RequiresStaging, long RequiredStagingBytes, string Reason);
public sealed record FilePackPreferences(int MaximumParallel = 2, string LastOutputDirectory = "",
    bool AskOutputEachTime = false, bool KeepExternalWorkspace = false)
{
    public bool IsEnabled { get; init; } = true;
    public KeyboardShortcut? ToggleShortcut { get; init; } = new(3, 0x50, "Ctrl + Alt + P");
    public QuickPackPreferences QuickPack { get; init; } = new();
    public Dictionary<string, string> SettingsColors { get; init; } = [];
    public Dictionary<string, string> PackWindowColors { get; init; } = [];
    public Dictionary<string, string> QuickWindowColors { get; init; } = [];
    public FilePackPreferences Normalize() => this with
    {
        MaximumParallel = Math.Clamp(MaximumParallel, 1, 4), LastOutputDirectory = LastOutputDirectory ?? "",
        ToggleShortcut = ToggleShortcut is null or { IsValid: true } ? ToggleShortcut : new(3, 0x50, "Ctrl + Alt + P"),
        QuickPack = (QuickPack ?? new()).Normalize(), SettingsColors = NormalizeColors(SettingsColors),
        PackWindowColors = NormalizeColors(PackWindowColors), QuickWindowColors = NormalizeColors(QuickWindowColors)
    };
    private static Dictionary<string, string> NormalizeColors(Dictionary<string, string>? colors) =>
        (colors ?? []).Where(pair => pair.Key.Length <= 32 && LensBorderColor.TryParse(pair.Value, out _))
            .Take(20).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
}

public sealed record QuickPackPreferences(PackFormat Format = PackFormat.Zip, PackCompression Compression = PackCompression.Standard,
    string OutputDirectory = "", string ArchiveName = "文件集装箱")
{
    public QuickPackPreferences Normalize() => this with
    {
        Format = Enum.IsDefined(Format) ? Format : PackFormat.Zip,
        Compression = Enum.IsDefined(Compression) ? Compression : PackCompression.Standard,
        OutputDirectory = OutputDirectory ?? "", ArchiveName = ArchiveName ?? "文件集装箱"
    };
}
