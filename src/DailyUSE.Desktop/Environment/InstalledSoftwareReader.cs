using DailyUSE.Core.Environment;
using Microsoft.Win32;

namespace DailyUSE.Desktop.Environment;

internal sealed record SoftwareInventory(IReadOnlyList<InstalledSoftwareInfo> Items,
    IReadOnlyList<ProbeIssue> Issues);

internal static class InstalledSoftwareReader
{
    public static SoftwareInventory Read(CancellationToken cancellationToken)
    {
        var items = new Dictionary<string, InstalledSoftwareInfo>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<ProbeIssue>();
        var locations = new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Registry64),
            (RegistryHive.CurrentUser, RegistryView.Registry32)
        };

        foreach (var (hive, view) in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var childName in uninstall.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var child = uninstall.OpenSubKey(childName);
                        var name = (child?.GetValue("DisplayName") as string)?.Trim();
                        if (string.IsNullOrWhiteSpace(name) || child?.GetValue("SystemComponent") is 1 ||
                            child?.GetValue("ParentKeyName") is string)
                            continue;
                        var version = child?.GetValue("DisplayVersion") as string;
                        var publisher = child?.GetValue("Publisher") as string;
                        items.TryAdd($"{name}\0{version}", new(name, version, publisher));
                    }
                    catch (Exception exception) when (exception is UnauthorizedAccessException or
                        System.Security.SecurityException or IOException)
                    {
                        AddIssue(issues);
                    }
                }
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or
                System.Security.SecurityException or IOException)
            {
                AddIssue(issues);
            }
        }

        return new(items.Values.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), issues);
    }

    private static void AddIssue(List<ProbeIssue> issues)
    {
        if (issues.Count == 0)
            issues.Add(new("软件", "部分软件安装记录无法读取"));
    }
}
