using System.Text.Json.Nodes;
using GitHub.Copilot;

namespace IsolatedCopilotRun;

internal sealed class UsageSummary
{
    private readonly HashSet<string> _models = [];
    private long? _input;
    private long? _output;
    private long? _reasoning;
    private long? _cacheRead;
    private long? _cacheWrite;
    private double? _modelMultiplierCost;

    public int Calls { get; private set; }

    public void Add(AssistantUsageData data)
    {
        Calls++;
        Add(ref _input, data.InputTokens);
        Add(ref _output, data.OutputTokens);
        Add(ref _reasoning, data.ReasoningTokens);
        Add(ref _cacheRead, data.CacheReadTokens);
        Add(ref _cacheWrite, data.CacheWriteTokens);
        if (data.Cost is { } cost)
        {
            _modelMultiplierCost = (_modelMultiplierCost ?? 0) + cost;
        }

        if (data.Model is { } model)
        {
            _models.Add(model);
        }
    }

    public JsonObject ToJson() => new()
    {
        ["telemetryStatus"] = Calls == 0 ? "unknown" : "reported",
        ["modelCalls"] = Calls == 0 ? null : Calls,
        ["models"] = _models.Count == 0 ? null : new JsonArray(_models.Select(model => (JsonNode)model).ToArray()),
        ["inputTokens"] = _input,
        ["outputTokens"] = _output,
        ["reasoningTokens"] = _reasoning,
        ["cacheReadTokens"] = _cacheRead,
        ["cacheWriteTokens"] = _cacheWrite,
        ["inputTokensIncludeCache"] = true,
        ["reasoningTokensAreOutputSubset"] = true,
        ["reportedModelMultiplierCost"] = _modelMultiplierCost,
        ["reportedModelMultiplierCostUnit"] = "model multiplier (not currency)"
    };

    private static void Add(ref long? total, long? value)
    {
        if (value is { } reported)
        {
            total = (total ?? 0) + reported;
        }
    }
}
