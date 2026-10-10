using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace DailyToolkit.Desktop.Runtime;

internal static class UpdateInstallation
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\DailyToolkit.Desktop_is1";

    internal static string? CurrentDirectory()
    {
        try
        {
            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var registration = user.OpenSubKey(UninstallKey);
            return MatchDirectory(System.Environment.ProcessPath, registration?.GetValue("InstallLocation") as string);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { Trace.WriteLine(error); return null; }
    }

    internal static string? MatchDirectory(string? executable, string? registeredDirectory)
    {
        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(registeredDirectory)) return null;
        try
        {
            var directory = Path.GetFullPath(registeredDirectory).TrimEnd(Path.DirectorySeparatorChar);
            return StartupRegistration.SameExecutable(Path.Combine(directory, "DailyToolkit.exe"), executable) ? directory : null;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException)
        { return null; }
    }

    internal static ProcessStartInfo InstallerStart(string setup, string directory)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(setup)) { UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(setup))! };
        foreach (var argument in new[] { "/SILENT", "/SP-", "/NORESTART", "/UPDATE=1", "/DIR=" + directory })
            start.ArgumentList.Add(argument);
        return start;
    }
}
