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
public sealed record PackBatch(string WorkingDirectory, IReadOnlyList<PackEntry> Entries);
public sealed record PackPlan(IReadOnlyList<PackBatch> Batches, bool RequiresStaging, long RequiredStagingBytes, string Reason);
public sealed record FilePackPreferences(int MaximumParallel = 2, string LastOutputDirectory = "",
    bool AskOutputEachTime = false, bool KeepExternalWorkspace = false)
{
    public FilePackPreferences Normalize() => this with
    {
        MaximumParallel = Math.Clamp(MaximumParallel, 1, 4), LastOutputDirectory = LastOutputDirectory ?? ""
    };
}
