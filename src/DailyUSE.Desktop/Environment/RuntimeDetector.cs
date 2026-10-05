using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using DailyUSE.Core.Environment;

namespace DailyUSE.Desktop.Environment;

internal static partial class RuntimeDetector
{
    public static async Task<IReadOnlyList<RuntimeInfo>> ReadAsync(CancellationToken cancellationToken)
    {
        var runtimes = new List<RuntimeInfo>
        {
            new("host-dotnet", ".NET（本应用）", RuntimeStatus.Available, System.Environment.Version.ToString())
        };
        foreach (var (id, name, executables, arguments) in new[]
        {
            ("python", "Python", new[] { "python.exe", "python3.exe" }, new[] { "--version" }),
            ("node", "Node.js", new[] { "node.exe" }, new[] { "--version" }),
            ("dotnet-sdk", ".NET SDK", new[] { "dotnet.exe" }, new[] { "--version" })
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (path, aliasFound) = FindExecutable(executables);
            if (path is null)
            {
                runtimes.Add(new(id, name, aliasFound ? RuntimeStatus.Unknown : RuntimeStatus.Missing, null));
                continue;
            }
            runtimes.Add(await ProbeVersionAsync(id, name, path, arguments, cancellationToken));
        }
        return runtimes;
    }

    private static (string? Path, bool AliasFound) FindExecutable(string[] fileNames)
    {
        var aliasFound = false;
        var directories = (System.Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Trim('"')).ToList();
        directories.Add(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "dotnet"));
        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (var fileName in fileNames)
            {
                var path = Path.Combine(directory, fileName);
                if (!File.Exists(path)) continue;
                if (path.Contains(@"\Microsoft\WindowsApps\", StringComparison.OrdinalIgnoreCase))
                {
                    aliasFound = true;
                    continue;
                }
                return (path, aliasFound);
            }
        }
        return (null, aliasFound);
    }

    private static async Task<RuntimeInfo> ProbeVersionAsync(string id, string name, string path,
        string[] arguments, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        using var process = new Process
        {
            StartInfo = new(path)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        if (id == "dotnet-sdk")
        {
            process.StartInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            process.StartInfo.Environment["DOTNET_NOLOGO"] = "1";
            process.StartInfo.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
            process.StartInfo.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        }
        try
        {
            if (!process.Start()) return new(id, name, RuntimeStatus.Unknown, null);
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error);
            var match = VersionPattern().Match(await output + "\n" + await error);
            return process.ExitCode == 0 && match.Success
                ? new RuntimeInfo(id, name, RuntimeStatus.Available, match.Value) { ExecutablePath = path }
                : new(id, name, RuntimeStatus.Unknown, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(id, name, RuntimeStatus.Unknown, null);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            return new(id, name, RuntimeStatus.Unknown, null);
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

    [GeneratedRegex(@"\d+\.\d+\.\d+(?:\.\d+)?")]
    private static partial Regex VersionPattern();
}
