using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using DailyToolkit.Core.FilePack;
using DailyToolkit.Desktop.FilePack;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private static async Task<int> RunFilePackCrashWorkerAsync(string root, string enginePath)
    {
        // Only the parent's newly created fixture can activate this subprocess mode.
        root = FilePackPaths.NormalizeSourcePath(root);
        FilePackPaths.EnsureNoReparsePoints(root);
        if (!FilePackPaths.IsWithin(root, Path.GetTempPath()) ||
            !Guid.TryParse(await File.ReadAllTextAsync(Path.Combine(root, ".filepack-crash-fixture")), out _)) return 2;
        var store = new FilePackStore(Path.Combine(root, "module"));
        var engine = new SevenZipAdapter(enginePath);
        var manager = new FilePackManager(store, engine);
        var source = Path.Combine(root, "source.bin");
        var entries = (await new FileCollectionService().CollectAsync([source])).Entries;
        var external = EngineSnapshot(entries, root, "external") with
        { Options = new(PackMode.External, PackFormat.Zip, PackCompression.Standard, root, "external", AllowStaging: true) };
        await manager.EnqueueAsync(external);
        await WaitPackStateAsync(manager, external.TaskId, task => task.State == PackState.AwaitingExternal);
        engine.ProcessStarted += pid =>
        {
            File.WriteAllText(Path.Combine(root, "crash-ready"), pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            // Hold the owned worker at a real native compression operation until its
            // parent terminates the test process tree, without WER or real input.
            Thread.Sleep(Timeout.Infinite);
        };
        var snapshot = EngineSnapshot(entries, root, "previous", PackFormat.SevenZip, PackCompression.Maximum) with
        { Options = new(PackMode.BuiltIn, PackFormat.SevenZip, PackCompression.Maximum, root, "previous",
            PackOutputPolicy.Overwrite, ApprovedOverwriteStamp: FilePackPaths.CaptureSourceStamp(Path.Combine(root, "previous.7z"))) };
        await manager.EnqueueAsync(snapshot);
        await Task.Delay(Timeout.Infinite);
        return 3;
    }

    private static async Task CheckFilePackCrashRecoveryAsync()
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-filepack-crash-");
        var root = fixture.FullName;
        Process? worker = null;
        Process? compression = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".filepack-crash-fixture"), Guid.NewGuid().ToString());
            var source = Path.Combine(root, "source.bin");
            await File.WriteAllBytesAsync(source, RandomNumberGenerator.GetBytes(8 * 1024 * 1024));
            var hash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            var previous = new byte[] { 5, 9, 13, 17 };
            await File.WriteAllBytesAsync(Path.Combine(root, "previous.7z"), previous);
            var enginePath = Path.Combine(AppContext.BaseDirectory, "tools", "7zip", "7za.exe");
            var start = new ProcessStartInfo(System.Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--filepack-crash-worker"); start.ArgumentList.Add(root); start.ArgumentList.Add(enginePath);
            worker = Process.Start(start) ?? throw new IOException("Could not launch isolated FilePack recovery worker");
            var stdout = worker.StandardOutput.ReadToEndAsync(); var stderr = worker.StandardError.ReadToEndAsync();
            var ready = Path.Combine(root, "crash-ready"); var timeout = Stopwatch.StartNew();
            while (!File.Exists(ready) && !worker.HasExited && timeout.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
            Require(File.Exists(ready), "Isolated crash worker did not reach native compression: " +
                (worker.HasExited ? await stderr : "timeout"));
            var pidText = await File.ReadAllTextAsync(ready);
            var pid = int.Parse(pidText, System.Globalization.CultureInfo.InvariantCulture);
            compression = Process.GetProcessById(pid);
            Require(!compression.HasExited, "Native compression exited before the parent termination test");
            worker.Kill(entireProcessTree: false); await worker.WaitForExitAsync();
            using (var timeoutToken = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                await compression.WaitForExitAsync(timeoutToken.Token);
            await Task.WhenAll(stdout, stderr);
            Require((await File.ReadAllBytesAsync(Path.Combine(root, "previous.7z"))).SequenceEqual(previous),
                "Abnormal process termination damaged the previous output archive");
            var store = new FilePackStore(Path.Combine(root, "module"));
            await using var recovered = new FilePackManager(store, new SevenZipAdapter(enginePath));
            await recovered.InitializeAsync();
            var stopped = recovered.Tasks.Single(task => task.Snapshot.Options.Mode == PackMode.BuiltIn);
            var external = recovered.Tasks.Single(task => task.Snapshot.Options.Mode == PackMode.External);
            Require(stopped.State == PackState.Interrupted && external.State == PackState.AwaitingExternal &&
                Directory.Exists(external.WorkspacePath), "Real termination was falsely restored as success or lost external staging");
            var restarted = await recovered.RetryAsync(stopped.TaskId);
            Require(restarted.Snapshot.SessionId != stopped.Snapshot.SessionId, "Restart reused a crashed session");
            Require((await WaitPackTerminalAsync(recovered, restarted.TaskId)).State == PackState.Completed,
                "Interrupted native task could not restart from the beginning");
            await recovered.CompleteExternalAsync(external.TaskId);
            Require(SHA256.HashData(await File.ReadAllBytesAsync(source)).SequenceEqual(hash), "Crash recovery modified the source file");
            Console.WriteLine("PASS FilePack real parent-only termination closes owned 7-Zip job, preserves previous output, restores external workspace and restarts a fresh session");
        }
        finally
        {
            if (worker is not null)
            {
                if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
                worker.Dispose();
            }
            if (compression is not null)
            {
                if (!compression.HasExited) { compression.Kill(entireProcessTree: true); await compression.WaitForExitAsync(); }
                compression.Dispose();
            }
            if (!FilePackPaths.IsWithin(root, Path.GetTempPath())) throw new IOException("Crash fixture escaped its temporary root");
            FilePackPaths.EnsureNoReparsePoints(root); fixture.Delete(recursive: true);
        }
    }

    private static async Task CheckFilePackExitRetryAsync()
    {
        var fixture = Directory.CreateTempSubdirectory("DailyToolkit-filepack-exit-");
        try
        {
            await using var model = new FilePackViewModel(new FilePackStore(fixture.FullName), "missing-engine.exe", _ => { });
            await model.InitializeAsync(); await model.FlushAsync();
            var denied = false;
            using (File.OpenHandle(Path.Combine(fixture.FullName, "preferences.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                try { await model.ShutdownAsync(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { denied = true; }
            Require(denied, "Exit fixture did not exercise a denied preference save");
            await model.ShutdownAsync();
            Require((await model.Store.LoadPreferencesAsync()).MaximumParallel == 2, "Retrying exit after unlocking the file lost preferences");
            Console.WriteLine("PASS FilePack failed exit save can be retried after the file becomes available");
        }
        finally { FilePackPaths.EnsureNoReparsePoints(fixture.FullName); fixture.Delete(recursive: true); }
    }
}
