using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace DailyUSE.Core.Tools;

public sealed record ToolRequest(string Operation, JsonElement Input, int ProtocolVersion = 1);
public sealed record ToolResponse(bool Success, JsonElement? Data, string? Error);
public sealed record ProcessBackend(string ExecutablePath, IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null);

public sealed class ToolRunException(string message) : Exception(message);

public sealed class ProcessToolRunner
{
    public async Task<ToolResponse> RunAsync(ProcessBackend backend, ToolRequest request,
        TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(backend.ExecutablePath) || !File.Exists(backend.ExecutablePath))
            throw new ToolRunException("工具程序不存在，或程序路径不是完整路径。");
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (request.ProtocolVersion != 1)
            throw new ToolRunException("工具通信版本不受支持。");

        var startInfo = new ProcessStartInfo(backend.ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = backend.WorkingDirectory ?? Path.GetDirectoryName(backend.ExecutablePath)!
        };
        foreach (var argument in backend.Arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
                throw new ToolRunException("工具程序未能启动。");

            // Drain both streams concurrently so a verbose backend cannot block the host.
            var outputTask = ReadLimitedAsync(process.StandardOutput, 1024 * 1024, deadline.Token);
            var errorTask = ReadLimitedAsync(process.StandardError, 128 * 1024, deadline.Token);
            await process.StandardInput.WriteLineAsync(
                JsonSerializer.Serialize(request).AsMemory(), deadline.Token);
            process.StandardInput.Close();

            // Observe a failed reader immediately, then the finally block terminates the backend.
            var pending = new List<Task> { process.WaitForExitAsync(deadline.Token), outputTask, errorTask };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                await completed;
                pending.Remove(completed);
            }
            if (process.ExitCode != 0)
                throw new ToolRunException($"工具运行失败（退出码 {process.ExitCode}）。");

            try
            {
                using var document = JsonDocument.Parse(await outputTask);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.EnumerateObject().Any(property =>
                        property.Name.Equals("success", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False))
                    throw new ToolRunException("工具返回的数据格式不正确。");
                return document.RootElement.Deserialize<ToolResponse>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new ToolRunException("工具没有返回结果。");
            }
            catch (JsonException)
            {
                throw new ToolRunException("工具返回的数据格式不正确。");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ToolRunException("工具运行超时。");
        }
        finally
        {
            try
            {
                if (process.Id != 0 && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int limit,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) is var count && count > 0)
        {
            if (text.Length + count > limit)
                throw new ToolRunException("工具返回的数据过多。");
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
