using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DailyToolkit.Core.Environment;

namespace DailyToolkit.Desktop.Environment;

public sealed class WindowsEnvironmentProbe : IEnvironmentProbe
{
    public MachineReport ReadBasic(DisplayInfo display)
    {
        var timer = Stopwatch.StartNew();
        var report = new MachineReport
        {
            System = NativeWindowsInfo.ReadSystem(), Cpu = NativeWindowsInfo.ReadCpu(),
            Memory = NativeWindowsInfo.ReadMemory(), Display = display, Power = NativeWindowsInfo.ReadPower(),
            Runtimes = [new("host-dotnet", ".NET（本应用）", RuntimeStatus.Available, System.Environment.Version.ToString())]
        };
        return report with { BasicReadMilliseconds = timer.Elapsed.TotalMilliseconds };
    }

    public async Task<MachineReport> ReadDetailsAsync(MachineReport basic, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var profile = AdaptationPolicy.Evaluate(basic, basic.Display.WorkAreaWidth);
        using var gate = new SemaphoreSlim(profile.WorkerLimit);
        var hardwareTask = WithGateAsync(gate, () => ReadHardwareAsync(cancellationToken), cancellationToken);
        var softwareTask = WithGateAsync(gate,
            () => Task.FromResult(InstalledSoftwareReader.Read(cancellationToken)), cancellationToken);
        var runtimeTask = WithGateAsync(gate, () => RuntimeDetector.ReadAsync(cancellationToken), cancellationToken);
        var volumeTask = WithGateAsync(gate, () => Task.FromResult(ReadVolumes(cancellationToken)), cancellationToken);
        await Task.WhenAll(hardwareTask, softwareTask, runtimeTask, volumeTask);
        cancellationToken.ThrowIfCancellationRequested();
        var hardware = await hardwareTask;
        var software = await softwareTask;
        var volumes = await volumeTask;
        var issues = basic.Issues.Concat(hardware.Issues).Concat(software.Issues).Concat(volumes.Issues).ToArray();
        var cpu = hardware.Cpu is { } detected
            ? basic.Cpu with
            {
                Name = string.IsNullOrWhiteSpace(detected.Name) ? basic.Cpu.Name : detected.Name,
                PhysicalCores = detected.PhysicalCores > 0 ? detected.PhysicalCores : null
            }
            : basic.Cpu;
        return basic with
        {
            Cpu = cpu, Graphics = hardware.Graphics, Volumes = volumes.Items,
            Runtimes = await runtimeTask, Software = software.Items, Issues = issues,
            SoftwareInventoryCompleted = software.Issues.Count == 0,
            Stage = issues.Length == 0 ? ProbeStage.Complete : ProbeStage.Partial,
            DetailReadMilliseconds = timer.Elapsed.TotalMilliseconds
        };
    }

    private static async Task<T> WithGateAsync<T>(SemaphoreSlim gate, Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try { return await action(); }
        finally { gate.Release(); }
    }

    private static VolumeInventory ReadVolumes(CancellationToken cancellationToken)
    {
        var volumes = new List<VolumeInfo>();
        var issues = new List<ProbeIssue>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (drive.DriveType != DriveType.Fixed) continue;
                    if (!drive.IsReady) { AddIssue(); continue; }
                    volumes.Add(new(drive.Name, drive.DriveFormat, drive.TotalSize, drive.AvailableFreeSpace));
                }
                catch (IOException) { AddIssue(); }
                catch (UnauthorizedAccessException) { AddIssue(); }
            }
        }
        catch (IOException) { AddIssue(); }
        catch (UnauthorizedAccessException) { AddIssue(); }
        return new(volumes, issues);

        void AddIssue()
        {
            if (issues.Count == 0) issues.Add(new("磁盘", "部分磁盘容量暂时无法读取"));
        }
    }

    private static async Task<HardwarePayload> ReadHardwareAsync(CancellationToken cancellationToken)
    {
        var powershell = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
            "DailyToolkit.Desktop.Environment.HardwareProbe.ps1")!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var script = await reader.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process
        {
            StartInfo = new(powershell)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            process.StartInfo.ArgumentList.Add(argument);
        try
        {
            if (!process.Start()) return HardwarePayload.Unavailable("暂时无法读取硬件详情");
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
            if (process.ExitCode != 0) return HardwarePayload.Unavailable("暂时无法读取硬件详情");
            return JsonSerializer.Deserialize<HardwarePayload>(await output, MachineReport.JsonOptions)
                ?? HardwarePayload.Unavailable("硬件详情没有返回结果");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HardwarePayload.Unavailable("硬件详情读取超时，基础信息仍可使用");
        }
        catch (Exception exception) when (exception is JsonException or IOException or
            System.ComponentModel.Win32Exception)
        {
            return HardwarePayload.Unavailable("暂时无法读取硬件详情");
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private sealed record HardwareCpu(string Name, int PhysicalCores, int LogicalProcessors);
    private sealed record VolumeInventory(IReadOnlyList<VolumeInfo> Items, IReadOnlyList<ProbeIssue> Issues);
    private sealed record HardwarePayload
    {
        public HardwareCpu? Cpu { get; init; }
        public GraphicsInfo[] Graphics { get; init; } = [];
        public ProbeIssue[] Issues { get; init; } = [];
        public static HardwarePayload Unavailable(string message) => new()
        {
            Issues = [new("硬件", message)]
        };
    }
}
