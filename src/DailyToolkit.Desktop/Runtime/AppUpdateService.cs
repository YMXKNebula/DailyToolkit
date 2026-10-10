using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace DailyToolkit.Desktop.Runtime;

internal sealed record AppUpdate(Version Version, string FileName, Uri DownloadUri, long Size, string Sha256);

internal sealed class AppUpdateService : IDisposable
{
    private const string Repository = "https://github.com/YMXKNebula/DailyToolkit";
    private readonly HttpClient _client;
    private readonly bool _ownsClient;

    internal AppUpdateService(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _client = client ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal async Task<AppUpdate?> CheckAsync(Version current, bool installed, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(new("https://api.github.com/repos/YMXKNebula/DailyToolkit/releases/latest"));
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (content.Length + count > 1024 * 1024) throw new InvalidDataException("更新信息过大。");
            content.Write(buffer, 0, count);
        }
        using var json = JsonDocument.Parse(content.ToArray());
        var release = json.RootElement;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() ||
            !System.Text.RegularExpressions.Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$") ||
            !Version.TryParse(tag[1..], out var version)) throw new InvalidDataException("没有找到正式版本信息。");
        if (version <= current) return null;
        var name = $"DailyToolkit-{version.ToString(3)}-win-x64-{(installed ? "setup.exe" : "portable.zip")}";
        var matches = release.GetProperty("assets").EnumerateArray()
            .Where(asset => asset.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("新版发布包尚未准备好，请稍后再试。");
        var asset = matches[0];
        var digest = asset.GetProperty("digest").GetString() ?? "";
        var size = asset.GetProperty("size").GetInt64();
        var address = asset.GetProperty("browser_download_url").GetString();
        if (!System.Text.RegularExpressions.Regex.IsMatch(digest, @"^sha256:[0-9a-fA-F]{64}$") ||
            size <= 0 || size > 1024L * 1024 * 1024 ||
            address != $"{Repository}/releases/download/{tag}/{name}")
            throw new InvalidDataException("新版发布包的信息不完整，请稍后再试。");
        return new(version, name, new(address), size, digest[7..].ToLowerInvariant());
    }

    internal async Task DownloadAsync(AppUpdate update, string destination, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        destination = Path.GetFullPath(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var response = await OpenDownloadAsync(update.DownloadUri, timeout.Token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != update.Size)
                throw new InvalidDataException("下载文件的大小不符，请重新检查更新。");
            await using var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                long downloaded = 0;
                var lastProgress = Stopwatch.StartNew();
                int count;
                while ((count = await source.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) != 0)
                {
                    downloaded += count;
                    if (downloaded > update.Size) throw new InvalidDataException("下载文件的大小不符，请重新检查更新。");
                    hash.AppendData(buffer, 0, count);
                    await target.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    if (lastProgress.ElapsedMilliseconds >= 100)
                    { progress?.Report(100d * downloaded / update.Size); lastProgress.Restart(); }
                }
                if (downloaded != update.Size || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != update.Sha256)
                    throw new InvalidDataException("下载校验未通过，文件没有用于更新。请重试。");
                await target.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            progress?.Report(100);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Trace.WriteLine(error); }
        }
    }

    private async Task<HttpResponseMessage> OpenDownloadAsync(Uri address, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            if (address.Scheme != "https" || !AllowedDownloadHost(address.Host))
                throw new InvalidDataException("下载地址不属于项目发布服务。");
            using var request = Request(address);
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("下载地址不可用。");
                address = location.IsAbsoluteUri ? location : new Uri(address, location);
                continue;
            }
            try { response.EnsureSuccessStatusCode(); return response; }
            catch { response.Dispose(); throw; }
        }
        throw new InvalidDataException("下载跳转过多，请稍后再试。");
    }

    private static bool AllowedDownloadHost(string host) => host is "github.com" or "release-assets.githubusercontent.com" or
        "objects.githubusercontent.com" or "github-releases.githubusercontent.com";

    private static HttpRequestMessage Request(Uri address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.UserAgent.ParseAdd("DailyToolkit-update");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
