using DailyUSE.Core.Environment;

namespace DailyUSE.Core.Tools;

public sealed record ToolDefinition(string Id, string Name, string Description,
    ToolRequirements Requirements);

public sealed class ToolCatalog
{
    private readonly Dictionary<string, ToolDefinition> _tools = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ToolDefinition> Tools => _tools.Values.ToArray();

    public void Register(ToolDefinition tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool.Id);
        if (!_tools.TryAdd(tool.Id, tool))
            throw new ArgumentException($"工具编号重复：{tool.Id}", nameof(tool));
    }

    public CompatibilityResult Check(string toolId, MachineReport report) =>
        _tools.TryGetValue(toolId, out var tool)
            ? CompatibilityEvaluator.Evaluate(tool.Requirements, report)
            : new(false, ["未找到这个工具"]);
}
