namespace Batonor.Abstractions;

/// <summary>How far a control-flow node has progressed.</summary>
public enum ExecutionPositionState
{
    /// <summary>The node has not been entered yet.</summary>
    Pending,
    /// <summary>The node is on the active execution path.</summary>
    Running,
    /// <summary>The node has finished.</summary>
    Completed,
}

/// <summary>
/// Serializable execution position: a chain of control-flow frames (one per actively-nested
/// control-flow node) leading to the suspended decision. Stored on a suspended
/// <see cref="WorkflowInstance"/> so it can resume exactly where it left off.
/// </summary>
public sealed class ExecutionPosition
{
    /// <summary>The control-flow node id this frame is about.</summary>
    public string NodeId { get; init; } = "";

    /// <summary>How far the control-flow node this frame represents has progressed.</summary>
    public ExecutionPositionState State { get; init; } = ExecutionPositionState.Running;

    /// <summary>For <c>sequence</c>: index of the step currently being run.</summary>
    public int? SequenceIndex { get; init; }

    /// <summary>For <c>choice</c>: the target node id the choice selected.</summary>
    public string? ChosenBranch { get; init; }

    /// <summary>For <c>decision</c>: the pending decision id that suspended the instance.</summary>
    public string? SuspendedDecisionId { get; init; }

    /// <summary>The deeper frame on the active path (the suspended subtree).</summary>
    public ExecutionPosition? Child { get; init; }
}
