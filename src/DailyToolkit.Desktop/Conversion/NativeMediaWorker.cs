using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DailyToolkit.Core.Conversion;

namespace DailyToolkit.Desktop.Conversion;

// MediaTranscoder has no Close/Dispose operation for its prepared pipeline.
// A short-lived owned process releases every WinRT reference on completion,
// cancellation and parent exit, without forcing GC across the application.
internal static class NativeMediaWorker
{
    internal const string Argument = "--convert-native-media";
    private const string RequestName = "media-request.json";

    internal static async Task<IReadOnlyList<ConvertedFile>> ConvertAsync(ConversionRequest request, string workspace,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var executable = System.Environment.ProcessPath ?? throw new IOException("无法启动系统转换程序。");
        var path = Path.Combine(workspace, RequestName);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            await JsonSerializer.SerializeAsync(file, request with { OutputDirectory = workspace }, cancellationToken: cancellationToken).ConfigureAwait(false);
        var arguments = new List<string>();
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var name = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
            if (string.IsNullOrWhiteSpace(name)) throw new IOException("无法找到系统转换程序。");
            var assembly = Path.Combine(AppContext.BaseDirectory, name + ".dll");
            if (!File.Exists(assembly)) throw new IOException("无法找到系统转换程序。");
            arguments.Add(assembly);
        }
        arguments.Add(Argument); arguments.Add(path);
        await ExternalConversionBackend.RunOwnedWorkerAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
        progress?.Report(100);
        return [new(Path.Combine(workspace, "converted." + request.Target), Path.GetFileNameWithoutExtension(request.Source) + "." + request.Target)];
    }

    internal static int Run(string[] arguments)
    {
        try
        {
            if (arguments is not [Argument, var path]) throw new IOException("转换请求无效。");
            path = ConversionRunner.LocalPath(path);
            var workspace = Path.GetDirectoryName(path)!;
            var directory = Path.GetFileName(workspace);
            if (Path.GetFileName(path) != RequestName || !directory.StartsWith(".dailytoolkit-convert-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(directory[".dailytoolkit-convert-".Length..], "N", out _) ||
                (File.GetAttributes(workspace) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 32768)
                throw new IOException("转换请求无效。");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var request = JsonSerializer.Deserialize<ConversionRequest>(file) ?? throw new IOException("转换请求无效。");
            if (request.Kind != ConversionKind.Media || request.OutputDirectory != workspace) throw new IOException("转换请求无效。");
            ConversionFormats.Validate(request.Kind, request.Source, request.Target);
            request = request with { Source = ConversionRunner.LocalPath(request.Source) };
            Task.Run(() => LocalConversionBackend.ConvertMediaInWorkerAsync(request, workspace, null, CancellationToken.None))
                .GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception exception)
        {
            var message = exception.Message;
            Console.Error.WriteLine(message[..Math.Min(message.Length, 1200)]);
            Trace.WriteLine(exception);
            return 1;
        }
    }
}
