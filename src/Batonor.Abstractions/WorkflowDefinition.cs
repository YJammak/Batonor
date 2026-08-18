using System.Text.Json.Nodes;

namespace Batonor.Abstractions;

/// <summary>
/// An immutable, versioned workflow definition. Definitions are treated as immutable once published;
/// a running instance snapshots the definition it started under.
/// </summary>
public sealed class WorkflowDefinition
{
    public string Id { get; init; } = "";
    public int Version { get; init; } = 1;
    public string? Description { get; init; }

    /// <summary>Initial variables, keyed by variable name.</summary>
    public JsonObject? Variables { get; init; }

    /// <summary>Overall instance time-to-live; exceeding it transitions the instance to <see cref="WorkflowStatus.TimedOut"/>.</summary>
    public TimeSpan? Timeout { get; init; }

    /// <summary>Flat list of nodes; control-flow nodes reference children by <see cref="WorkflowNode.Id"/>.</summary>
    public IReadOnlyList<WorkflowNode> Steps { get; init; } = Array.Empty<WorkflowNode>();
}

/// <summary>A single node in a workflow: either a control-flow node or an activity.</summary>
public sealed class WorkflowNode
{
    public string Id { get; init; } = "";

    /// <summary>
    /// Node kind: a control-flow type (<c>sequence</c>, <c>parallel</c>, <c>choice</c>, <c>decision</c>,
    /// <c>foreach</c>, <c>while</c>) or a registered activity name.
    /// </summary>
    public string Type { get; init; } = "";

    /// <summary>Node-specific configuration (activity parameters / branches / conditions).</summary>
    public JsonNode? Config { get; init; }

    /// <summary>Variable name the node output is written to, if any.</summary>
    public string? Output { get; init; }

    /// <summary>Recovery semantics (see <see cref="RecoveryPolicy"/>).</summary>
    public RecoveryPolicy Recovery { get; init; } = RecoveryPolicy.AtLeastOnce;

    /// <summary>Error handling policy.</summary>
    public NodeErrorHandling? OnError { get; init; }
}

/// <summary>Node-level error handling.</summary>
public sealed class NodeErrorHandling
{
    /// <summary>Number of retries before failing.</summary>
    public int Retry { get; init; }

    /// <summary>Delay between retries, in milliseconds.</summary>
    public int IntervalMs { get; init; } = 1000;

    /// <summary>If true, a failure aborts sibling branches immediately (parallel).</summary>
    public bool FailFast { get; init; }
}
