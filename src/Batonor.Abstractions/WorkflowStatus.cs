namespace Batonor.Abstractions;

/// <summary>Lifecycle state of a workflow instance.</summary>
public enum WorkflowStatus
{
    Running,
    Suspended,
    Completed,
    Failed,
    Cancelled,
    TimedOut,
}
