using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DailyToolkit.Desktop.Runtime;

internal enum DiagnosticModule { Application, ScreenLens, FloatingNotes, ClipRecall, FilePack, Computer, Update, Diagnostics }
internal enum DiagnosticErrorCategory { Canceled, AccessDenied, Storage, InvalidData, Native, Timeout, Resource, Unexpected }
internal sealed record DiagnosticModuleState(DiagnosticModule Module, bool Enabled);
internal sealed record DiagnosticError(long Minute, DiagnosticModule Module, DiagnosticErrorCategory Category);
internal sealed record DiagnosticResources(long? WorkingSetMiB, long? PrivateMemoryMiB, int? Handles, int? Threads, long? CpuSeconds);
internal sealed record DiagnosticReport(int SchemaVersion, string Version, long UptimeMinutes,
    DiagnosticModuleState[] Modules, DiagnosticResources Resources, DiagnosticError[] RecentErrors);

/// <summary>
/// Stores only fixed error categories in a bounded memory queue. No timer, disk journal,
/// message, stack trace, user content, file path or machine identifier is collected.
/// A report is written only after the user selects its destination.
/// </summary>
internal sealed class DiagnosticReportService
{
    internal const int MaximumErrors = 64;
    internal static DiagnosticReportService Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Queue<DiagnosticError> _errors = new(MaximumErrors);
    private readonly long _started = Stopwatch.GetTimestamp();
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    internal void RecordError(DiagnosticModule module, Exception? exception)
    {
        if (exception is null) return;
        var entry = new DiagnosticError(UptimeMinutes, Normalize(module), Classify(exception));
        lock (_gate)
        {
            if (_errors.Count == MaximumErrors) _errors.Dequeue();
            _errors.Enqueue(entry);
        }
    }

    internal void ClearRecentErrors() { lock (_gate) _errors.Clear(); }
    private long UptimeMinutes => (long)Stopwatch.GetElapsedTime(_started).TotalMinutes;

    internal DiagnosticReport Capture(IEnumerable<DiagnosticModuleState> states)
    {
        DiagnosticError[] recent;
        lock (_gate) recent = _errors.ToArray();
        // Only a numeric assembly version is allowed; no build path or custom metadata.
        var version = typeof(DiagnosticReportService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var modules = states.Select(state => new DiagnosticModuleState(Normalize(state.Module), state.Enabled))
            .DistinctBy(state => state.Module).OrderBy(state => state.Module).ToArray();
        return new(1, version, UptimeMinutes, modules, CaptureResources(), recent);
    }

    internal async Task ExportAsync(string destination, IEnumerable<DiagnosticModuleState> states, CancellationToken token = default)
    {
        // Stage beside the selected file. A canceled/failed write leaves an existing report intact.
        var report = Capture(states);
        destination = Path.GetFullPath(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, report, JsonOptions, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static DiagnosticModule Normalize(DiagnosticModule module) =>
        Enum.IsDefined(module) ? module : DiagnosticModule.Application;

    private static DiagnosticErrorCategory Classify(Exception exception) => exception switch
    {
        OperationCanceledException => DiagnosticErrorCategory.Canceled,
        UnauthorizedAccessException => DiagnosticErrorCategory.AccessDenied,
        InvalidDataException or JsonException => DiagnosticErrorCategory.InvalidData,
        IOException => DiagnosticErrorCategory.Storage,
        Win32Exception or System.Runtime.InteropServices.ExternalException => DiagnosticErrorCategory.Native,
        TimeoutException => DiagnosticErrorCategory.Timeout,
        OutOfMemoryException => DiagnosticErrorCategory.Resource,
        _ => DiagnosticErrorCategory.Unexpected
    };

    private static DiagnosticResources CaptureResources()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            return new(RoundMemory(process.WorkingSet64), RoundMemory(process.PrivateMemorySize64),
                RoundCount(process.HandleCount, 10), RoundCount(process.Threads.Count, 4),
                (long)Math.Ceiling(process.TotalProcessorTime.TotalSeconds));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Unavailable counters are represented as null, never invented zero measurements.
            return new(null, null, null, null, null);
        }
    }

    private static long RoundMemory(long bytes) => ((bytes + (8L << 20) - 1) / (8L << 20)) * 8;
    private static int RoundCount(int count, int bucket) => (int)Math.Min(int.MaxValue, ((long)count + bucket - 1) / bucket * bucket);
}
