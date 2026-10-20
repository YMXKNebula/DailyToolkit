using System.Globalization;

namespace DailyToolkit.Core.FilePack;

public static class PackArchiveNaming
{
    public const string DefaultName = "集装箱-{完成时间}";

    public static bool UsesCompletionTime(string? name) => string.IsNullOrWhiteSpace(name) ||
        string.Equals(name.Trim(), DefaultName, StringComparison.Ordinal);

    public static string NormalizeDefault(string? name) => string.IsNullOrWhiteSpace(name) || name.Trim() == "文件集装箱"
        ? DefaultName : name;

    public static string AtCompletion(DateTimeOffset finishedAt) =>
        "集装箱-" + finishedAt.ToLocalTime().ToString("yyyy-MM-dd-HH：mm", CultureInfo.InvariantCulture);
}
