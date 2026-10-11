using DailyToolkit.Core.Gaming;

namespace DailyToolkit.Core.Clipboard;

public sealed record ClipboardHistorySettings
{
    public int SchemaVersion { get; init; } = 1;
    public bool IsEnabled { get; init; }
    public bool ConsentAccepted { get; init; }
    public bool IsPaused { get; init; }
    public KeyboardShortcut? ToggleShortcut { get; init; } = new(3, 0x56, "Ctrl + Alt + V");
    public int MaximumRecords { get; init; } = 1000;
    public int RetentionDays { get; init; } = 30;
    public int MaximumOrdinaryMegabytes { get; init; } = 200;
    public int MaximumTextKilobytes { get; init; } = 1024;
    public int MaximumImageMegabytes { get; init; } = 8;
    public bool UseCachedFiles { get; init; }
    public int MaximumCachedFileMegabytes { get; init; } = 100;
    public int MaximumFileCacheMegabytes { get; init; } = 200;
    public bool IncludeRestoredInWindowsHistory { get; init; }
    public string[] ExcludedApplications { get; init; } = [];
    public Dictionary<string, string> PanelColors { get; init; } = [];

    public ClipboardHistorySettings Normalize() => this with
    {
        IsEnabled = IsEnabled && ConsentAccepted,
        ToggleShortcut = ToggleShortcut is null or { IsValid: true } ? ToggleShortcut : new ClipboardHistorySettings().ToggleShortcut,
        MaximumRecords = Math.Clamp(MaximumRecords, 1, 100000),
        RetentionDays = Math.Clamp(RetentionDays, 1, 3650),
        MaximumOrdinaryMegabytes = Math.Clamp(MaximumOrdinaryMegabytes, 1, 10240),
        MaximumTextKilobytes = Math.Clamp(MaximumTextKilobytes, 1, 10240),
        MaximumImageMegabytes = Math.Clamp(MaximumImageMegabytes, 1, 256),
        MaximumCachedFileMegabytes = Math.Clamp(MaximumCachedFileMegabytes, 1, 10240),
        MaximumFileCacheMegabytes = Math.Clamp(MaximumFileCacheMegabytes, 1, 10240),
        ExcludedApplications = (ExcludedApplications ?? []).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray(),
        PanelColors = (PanelColors ?? []).Where(pair => pair.Key.Length <= 32 && LensBorderColor.TryParse(pair.Value, out _))
            .Take(32).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
    };
}
