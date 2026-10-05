using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DailyUSE.Core.Environment;
using Microsoft.CSharp.RuntimeBinder;

namespace DailyUSE.Desktop.Environment;

public sealed class WindowsLocalStatusProbe : ILocalStatusProbe
{
    private readonly TrafficSampler _traffic = new();
    private readonly Stopwatch _timer = Stopwatch.StartNew();

    public Task<NetworkInfo> ReadNetworkAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = ReadWindowsNetworkState();
        var counters = new List<LinkCounter>();
        var kinds = new HashSet<string>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                try
                {
                    if (!adapter.GetIPProperties().GatewayAddresses.Any(gateway =>
                        !gateway.Address.Equals(IPAddress.Any) && !gateway.Address.Equals(IPAddress.IPv6Any)))
                        continue;
                    var statistics = adapter.GetIPStatistics();
                    counters.Add(new(adapter.Id, statistics.BytesReceived, statistics.BytesSent));
                    kinds.Add(adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi" :
                        adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet
                            ? "有线网络" : "其他连接");
                }
                catch (NetworkInformationException) { }
            }
        }
        catch (NetworkInformationException) { }
        var rate = _traffic.Sample(counters, _timer.Elapsed.TotalSeconds);
        return new NetworkInfo(state, string.Join(" / ", kinds.Order()), rate.ReceivedBytesPerSecond, rate.SentBytesPerSecond);
    }, cancellationToken);

    private static NetworkState ReadWindowsNetworkState()
    {
        object? manager = null;
        try
        {
            var managerType = Type.GetTypeFromCLSID(new("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            manager = Activator.CreateInstance(managerType!);
            dynamic windows = manager!;
            return windows.IsConnectedToInternet ? NetworkState.Internet :
                windows.IsConnected ? NetworkState.LocalNetwork : NetworkState.Disconnected;
        }
        catch (Exception exception) when (exception is COMException or RuntimeBinderException or
            UnauthorizedAccessException or TypeLoadException)
        {
            return NetworkState.Unknown;
        }
        finally { if (manager is not null && Marshal.IsComObject(manager)) Marshal.FinalReleaseComObject(manager); }
    }

    public async Task<WeatherInfo> ReadWeatherAsync(CancellationToken cancellationToken)
    {
        var executable = System.Environment.ProcessPath;
        if (executable is null || !Path.GetFileName(executable).Equals("DailyUSE.exe", StringComparison.OrdinalIgnoreCase))
            return WeatherInfo.Unavailable("Windows 天气读取程序不可用");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var process = new Process
        {
            StartInfo = new(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add("--read-windows-weather");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) return WeatherInfo.Unavailable("Windows 天气文字暂时不可读");
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
            var json = await output;
            return process.ExitCode == 0 && json.Length <= 8192
                ? JsonSerializer.Deserialize<WeatherInfo>(json, MachineReport.JsonOptions)
                    ?? WeatherInfo.Unavailable("Windows 任务栏未提供天气文字")
                : WeatherInfo.Unavailable("Windows 天气文字暂时不可读");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WeatherInfo.Unavailable("Windows 天气文字读取超时");
        }
        catch (Exception exception) when (exception is JsonException or IOException or System.ComponentModel.Win32Exception)
        {
            return WeatherInfo.Unavailable("Windows 天气文字暂时不可读");
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
}
