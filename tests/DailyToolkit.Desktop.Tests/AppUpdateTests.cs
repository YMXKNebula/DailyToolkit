using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DailyToolkit.Desktop.Presentation;
using DailyToolkit.Desktop.Runtime;

namespace DailyToolkit.Desktop.Tests;

internal static partial class Program
{
    private sealed class UpdateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests++; return send(request, cancellationToken); }
    }

    private sealed class PausedUpdateStream(byte[] data) : Stream
    {
        private bool _first = true;
        internal readonly TaskCompletionSource Paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (_first)
            {
                _first = false;
                var length = data.Length / 2;
                data.AsMemory(0, length).CopyTo(buffer);
                return length;
            }
            Paused.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string UpdateMetadata(byte[] payload, string version = "0.8.0", string? hash = null,
        string? downloadHost = null, bool prerelease = false)
    {
        var digest = hash ?? Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        return JsonSerializer.Serialize(new
        {
            tag_name = "v" + version, draft = false, prerelease,
            assets = new[] { "setup.exe", "portable.zip" }.Select(kind => new
            {
                name = $"DailyToolkit-{version}-win-x64-{kind}", size = payload.LongLength, digest = "sha256:" + digest,
                browser_download_url = $"{downloadHost ?? "https://github.com"}/YMXKNebula/DailyToolkit/releases/download/v{version}/DailyToolkit-{version}-win-x64-{kind}"
            }).ToArray()
        });
    }

    private static HttpResponseMessage UpdateResponse(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static async Task ExpectUpdateFailureAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid update input was accepted.");
    }

    private static async Task CheckAppUpdatesAsync()
    {
        var payload = Encoding.UTF8.GetBytes("isolated update package fixture");
        var root = Path.Combine(Path.GetTempPath(), "DailyToolkit-update-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var handler = new UpdateHandler((_, _) => Task.FromResult(UpdateResponse(UpdateMetadata(payload)))))
            using (var client = new HttpClient(handler))
            using (var service = new AppUpdateService(client))
            {
                var installed = await service.CheckAsync(new(0, 7, 9), true, default);
                var portable = await service.CheckAsync(new(0, 7, 9), false, default);
                Require(installed?.FileName.EndsWith("-setup.exe") == true && portable?.FileName.EndsWith("-portable.zip") == true,
                    "Installation and portable updates selected the same package.");
                Require(await service.CheckAsync(new(0, 8, 0), true, default) is null &&
                    await service.CheckAsync(new(0, 9, 0), false, default) is null, "Update offered an equal or older version.");
            }
            foreach (var metadata in new[] { UpdateMetadata(payload, prerelease: true),
                UpdateMetadata(payload, downloadHost: "https://untrusted.invalid"), UpdateMetadata(payload, hash: "bad") })
            {
                using var handler = new UpdateHandler((_, _) => Task.FromResult(UpdateResponse(metadata)));
                using var client = new HttpClient(handler); using var service = new AppUpdateService(client);
                await ExpectUpdateFailureAsync(async () => { _ = await service.CheckAsync(new(0, 7, 9), true, default); });
            }
            Console.WriteLine("PASS Updates select matching release assets and reject older, preview or invalid releases");

            var update = new AppUpdate(new(0, 8, 0), "fixture.zip",
                new("https://github.com/YMXKNebula/DailyToolkit/releases/download/v0.8.0/fixture.zip"),
                payload.Length, Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
            var destination = Path.Combine(root, "fixture.zip");
            using (var handler = new UpdateHandler((request, _) =>
            {
                if (request.RequestUri!.Host == "github.com")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                    redirect.Headers.Location = new("https://release-assets.githubusercontent.com/test-fixture");
                    return Task.FromResult(redirect);
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
            }))
            using (var client = new HttpClient(handler))
            using (var service = new AppUpdateService(client))
            {
                await service.DownloadAsync(update, destination, null, default);
                Require(File.ReadAllBytes(destination).SequenceEqual(payload) && Directory.GetFiles(root, "*.part").Length == 0,
                    "Verified download did not commit exact bytes or left temporary files.");
            }
            Console.WriteLine("PASS Updates follow official asset redirects and commit only verified downloads");

            foreach (var badUpdate in new[] { update with { Sha256 = new string('0', 64) }, update with { Size = payload.Length + 1 } })
            {
                File.WriteAllText(destination, "keep existing download");
                using var handler = new UpdateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new ByteArrayContent(payload) }));
                using var client = new HttpClient(handler); using var service = new AppUpdateService(client);
                await ExpectUpdateFailureAsync(() => service.DownloadAsync(badUpdate, destination, null, default));
                Require(File.ReadAllText(destination) == "keep existing download" && Directory.GetFiles(root, "*.part").Length == 0,
                    "Failed verification replaced an existing file or left a partial download.");
            }
            using (var handler = new UpdateHandler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Found);
                response.Headers.Location = new("https://untrusted.invalid/package");
                return Task.FromResult(response);
            }))
            using (var client = new HttpClient(handler))
            using (var service = new AppUpdateService(client))
            {
                await ExpectUpdateFailureAsync(() => service.DownloadAsync(update, destination, null, default));
                Require(handler.Requests == 1, "Untrusted redirect received a network request.");
            }
            Console.WriteLine("PASS Failed download size/hash checks preserve existing files and reject untrusted redirects");

            using (var source = new PausedUpdateStream(payload))
            using (var handler = new UpdateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(source) })))
            using (var client = new HttpClient(handler))
            using (var service = new AppUpdateService(client))
            using (var cancel = new CancellationTokenSource())
            {
                var pending = service.DownloadAsync(update, destination, null, cancel.Token);
                await source.Paused.Task;
                Require(Directory.GetFiles(root, "*.part").Length == 1, "Cancellation did not exercise an in-progress partial file.");
                cancel.Cancel();
                try { await pending; throw new InvalidOperationException("Cancelled update continued."); }
                catch (OperationCanceledException) { }
                Require(File.ReadAllText(destination) == "keep existing download" && Directory.GetFiles(root, "*.part").Length == 0,
                    "Cancelled update changed the previous download.");
            }
            Console.WriteLine("PASS Update cancellation leaves existing files intact and removes partial downloads");

            var installedDirectory = Path.Combine(root, "安装目录");
            var installedExe = Path.Combine(installedDirectory, "DailyToolkit.exe");
            Require(UpdateInstallation.MatchDirectory(installedExe, installedDirectory) == installedDirectory &&
                UpdateInstallation.MatchDirectory(Path.Combine(root, "portable", "DailyToolkit.exe"), installedDirectory) is null &&
                UpdateInstallation.MatchDirectory(installedExe, null) is null, "A portable copy inherited another installation's update mode.");
            var start = UpdateInstallation.InstallerStart(Path.Combine(root, "setup.exe"), installedDirectory);
            Require(start.UseShellExecute && start.ArgumentList.Contains("/SILENT") && start.ArgumentList.Contains("/NORESTART") &&
                start.ArgumentList.Contains("/UPDATE=1") && start.ArgumentList.Contains("/DIR=" + installedDirectory) &&
                !start.ArgumentList.Any(argument => argument.Contains("TASKS") || argument.Contains("MIGRATE")),
                "Installer update lost the installation path, restart request, or changed startup choices.");
            Console.WriteLine("PASS Update mode follows the running executable and preserves installation/startup choices");

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var handler = new UpdateHandler(async (_, token) =>
            { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("Unreachable"); }))
            using (var client = new HttpClient(handler))
            using (var model = new AppUpdateViewModel(new(client)))
            {
                Require(handler.Requests == 0 && model.ActionText == "检查更新", "Update checked the network before a click.");
                var first = model.RunAsync(); await entered.Task;
                await model.RunAsync();
                Require(handler.Requests == 1 && model.IsBusy && model.CanCancel && !model.CanUpdate,
                    "Repeated clicks overlapped update operations.");
                model.Cancel(); await first;
                Require(!model.IsBusy && model.CanUpdate && model.Status == "已取消更新。", "Cancelled update left controls busy.");
            }
            using (var handler = new UpdateHandler((_, _) => Task.FromResult(UpdateResponse(UpdateMetadata(payload)))))
            using (var client = new HttpClient(handler))
            using (var model = new AppUpdateViewModel(new(client)))
            {
                await model.RunAsync();
                Require(model.ActionText == "更新" && model.Status.Contains("0.8.0") && model.CanUpdate,
                    "New release did not enable the update button.");
            }
            Console.WriteLine("PASS Update controls avoid background requests, serialize clicks and recover after cancellation");
        }
        finally
        {
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("DailyToolkit-update-check-", StringComparison.Ordinal)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CheckLiveAppUpdatesAsync(string directory)
    {
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\.local")) + "\\";
        directory = Path.GetFullPath(directory);
        Require(directory.StartsWith(workspace, StringComparison.OrdinalIgnoreCase), "Live update output escaped the workspace.");
        Directory.CreateDirectory(directory);
        using var service = new AppUpdateService();
        var installed = await service.CheckAsync(new(0, 0, 0), true, default) ?? throw new InvalidOperationException("No release.");
        var portable = await service.CheckAsync(new(0, 0, 0), false, default) ?? throw new InvalidOperationException("No release.");
        Require(installed.Version == portable.Version && await service.CheckAsync(installed.Version, true, default) is null,
            "Live release selection or comparison failed.");
        foreach (var update in new[] { installed, portable })
        {
            await service.DownloadAsync(update, Path.Combine(directory, update.FileName), null, default);
            Console.WriteLine("PASS Live official GitHub metadata, HTTPS download and SHA-256: " + update.FileName);
        }
        File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new
        { Passed = true, installed.Version, Packages = new[] { installed, portable }, Executed = false }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
