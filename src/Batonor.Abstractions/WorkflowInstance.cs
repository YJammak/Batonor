using System.Text.Json.Nodes;

namespace Batonor.Abstractions;

/// <summary>
/// A running (or finished) workflow instance. Holds the definition snapshot it started under
/// and its current variables and status.
/// </summary>
public sealed class WorkflowInstance
{
    public string InstanceId { get; init; } = "";
    public string DefinitionId { get; init; } = "";
    public int DefinitionVersion { get; init; } = 1;
    public WorkflowStatus Status { get; set; } = WorkflowStatus.Running;

    /// <summary>Current workflow variables.</summary>
    public JsonObject? Variables { get; set; }

    /// <summary>
    /// Execution position when <see cref="Status"/> is <see cref="WorkflowStatus.Suspended"/>.
    /// Null for instances that have never suspended.
    /// </summary>
    public ExecutionPosition? Position { get; set; }

    /// <summary>Error message when <see cref="Status"/> is <see cref="WorkflowStatus.Failed"/>.</summary>
    public string? Error { get; set; }

    /// <summary>Immutable definition snapshot this instance runs under.</summary>
    public WorkflowDefinition? Definition { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}
