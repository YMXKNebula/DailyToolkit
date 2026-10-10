using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Win32;

namespace DailyToolkit.InstallerHost;

internal static class AdminInstallerChecks
{
    private const string Identity = "DailyToolkit.InstallerTests";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Identity + "_is1";

    internal static int Run(string setup, string directory, string portable, string report)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var taskName = Identity + "-" + sid;
        object? scheduler = null;
        dynamic? folder = null;
        var taskCreated = false;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\.."));
        var expectedRoot = Path.Combine(root, ".local") + Path.DirectorySeparatorChar;
        directory = Path.GetFullPath(directory);
        report = Path.GetFullPath(report);
        try
        {
            Require(new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator), "Administrator permission was not granted.");
            Require(directory.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) &&
                report.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFullPath(setup).StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase), "Test paths escaped the workspace.");
            using var existing = Registry.CurrentUser.OpenSubKey(UninstallKey);
            Require(existing is null, "Another test installation is still registered.");
            scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            ((dynamic)scheduler).Connect(); folder = ((dynamic)scheduler).GetFolder(@"\");
            Require(!TaskExists(folder, taskName), "Another test task is still registered.");
            Require(RunProcess(setup, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/TASKS=", "/DIR=" + directory,
                "/LOG=" + report + ".install.log"]) == 0, "Isolated installation failed.");
            dynamic definition = ((dynamic)scheduler).NewTask(0);
            definition.RegistrationInfo.Description = "DailyToolkit automatic startup. Owner: " + sid;
            definition.Principal.UserId = sid;
            definition.Principal.LogonType = 3;
            definition.Principal.RunLevel = 1;
            definition.Settings.AllowHardTerminate = false;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            dynamic trigger = definition.Triggers.Create(9);
            trigger.UserId = sid; trigger.Enabled = false; // This test never starts at logon.
            dynamic action = definition.Actions.Create(0);
            action.Path = portable;
            action.Arguments = "--startup --admin-task";
            action.WorkingDirectory = Path.GetDirectoryName(portable);
            folder.RegisterTaskDefinition(taskName, definition, 6, sid, null, 3,
                $"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFXSD;;;{sid})");
            taskCreated = true;
            dynamic beforeTask = folder.GetTask(taskName);
            var beforeXml = (string)beforeTask.Xml;
            var beforeSecurity = (string)beforeTask.GetSecurityDescriptor(7);
            Require(RunProcess(setup, ["/MIGRATEONLY=1", "/OWNER=" + sid, "/TARGET=" + directory,
                "/LOG=" + report + ".migrate.log"]) == 0, "Administrator maintenance helper failed.");
            dynamic afterTask = folder.GetTask(taskName);
            Require((string)afterTask.Definition.Actions[1].Path == Path.Combine(directory, "DailyToolkit.exe") &&
                (string)afterTask.Definition.Actions[1].WorkingDirectory == directory, "Administrator task did not move to the installation.");
            Require((string)afterTask.GetSecurityDescriptor(7) == beforeSecurity, "Migration changed the task's security descriptor.");
            Require(XNode.DeepEquals(WithoutPaths(beforeXml), WithoutPaths((string)afterTask.Xml)),
                "Migration changed task fields other than program path and working directory.");
            var uninstaller = ReadUninstaller(directory);
            Require(RunProcess(uninstaller, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                "/LOG=" + report + ".uninstall.log"]) == 0, "Administrator-task uninstall failed.");
            Require(!TaskExists(folder, taskName) && !File.Exists(Path.Combine(directory, "DailyToolkit.exe")),
                "Uninstall left the administrator task or application.");
            taskCreated = false;
            File.WriteAllText(report, JsonSerializer.Serialize(new { Passed = true, Owner = sid,
                Checks = new[] { "highest-level task migration", "only two action paths changed", "SID, triggers, run level, arguments, power settings and SDDL retained", "owned highest-level task removed during uninstall" },
                Scope = "UAC-consented isolated host exercised the elevated maintenance helper. Unelevated setup's ShellExec handoff was not exercised." }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            if (report.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(report + ".error.txt", error.ToString());
            return 1;
        }
        finally
        {
            if (folder is not null && taskCreated)
            {
                try { folder.DeleteTask(taskName, 0); } catch (Exception error) { Trace.WriteLine(error); }
            }
            try
            {
                if (directory.StartsWith(expectedRoot, StringComparison.OrdinalIgnoreCase) && ReadUninstaller(directory) is { } uninstall)
                    _ = RunProcess(uninstall, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART"]);
            }
            catch (Exception error) { Trace.WriteLine(error); }
            if (scheduler is not null) Marshal.FinalReleaseComObject(scheduler);
        }
    }

    private static XElement WithoutPaths(string xml)
    {
        var document = XElement.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        foreach (var element in document.Descendants().Where(e => e.Name == ns + "Command" || e.Name == ns + "WorkingDirectory"))
            element.Value = "<path>";
        return document;
    }

    private static string ReadUninstaller(string directory)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        var command = (string?)key?.GetValue("UninstallString") ?? throw new InvalidOperationException("Test uninstall registration is absent.");
        var executable = command.Trim('"');
        Require(Path.GetDirectoryName(executable)?.TrimEnd('\\') == directory.TrimEnd('\\') &&
            Path.GetFileName(executable).StartsWith("unins", StringComparison.OrdinalIgnoreCase), "Uninstall escaped the test directory.");
        return executable;
    }

    private static bool TaskExists(dynamic folder, string name)
    {
        dynamic tasks = folder.GetTasks(1);
        for (var index = 1; index <= tasks.Count; index++) if ((string)tasks[index].Name == name) return true;
        return false;
    }

    private static int RunProcess(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Test process did not start.");
        Require(process.WaitForExit(45000), "Test process did not finish; no forced termination was used.");
        return process.ExitCode;
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
