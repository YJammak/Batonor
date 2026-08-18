namespace Batonor.Abstractions;

/// <summary>A decision awaiting human input, persisted while the instance is suspended.</summary>
public sealed class PendingDecision
{
    public string DecisionId { get; init; } = "";
    public string InstanceId { get; init; } = "";
    public string NodeId { get; init; } = "";
    public string? Prompt { get; init; }
    public IReadOnlyList<DecisionOption> Options { get; init; } = Array.Empty<DecisionOption>();

    /// <summary>Absolute time after which the decision times out (null = no timeout).</summary>
    public DateTimeOffset? TimeoutAt { get; init; }
}

public sealed class DecisionOption
{
    public string Label { get; init; } = "";
    public string Value { get; init; } = "";
    public bool IsDefault { get; init; }
}
